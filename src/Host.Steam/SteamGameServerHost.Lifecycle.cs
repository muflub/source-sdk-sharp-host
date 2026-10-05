using System.Globalization;
using System.Runtime.InteropServices;
using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

public sealed partial class SteamGameServerHost
{
    /// <summary>
    /// Loads the native library, initializes the game server interface, applies all settings,
    /// logs on and waits until Steam acknowledges the server.
    /// Throws <see cref="ArgumentNullException"/> for a null config,
    /// <see cref="SteamNativeException"/> when the library can't load or init fails,
    /// <see cref="SteamApiException"/> on logon timeout; <see cref="OperationCanceledException"/>
    /// if <paramref name="cancellationToken"/> fires first (host is left Faulted).
    /// </summary>
    public async Task StartAsync(GameServerConfiguration config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (State != GameServerState.Created)
            throw new InvalidOperationException($"Cannot start: host is {State}.");
        _config = config;
        SetState(GameServerState.Starting, "starting");

        PrepareAppIdEnvironment(config);

        try
        {
            _api = SteamNativeApi.Load(config.NativeLibraryPath);
        }
        catch
        {
            SetState(GameServerState.Faulted, "native library load failed");
            throw;
        }

        _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _logonTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _logoffTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = "steam-gs-pump" };
        _pumpThread.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(config.CallTimeout);
        try
        {
            await _logonTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetState(GameServerState.Faulted, "logon timed out");
            throw new SteamApiException(SteamResult.Timeout,
                $"Game server did not log on within {config.CallTimeout}. Check AppId, connectivity, and the GSLT for non-anonymous logon.");
        }
        catch (OperationCanceledException)
        {
            SetState(GameServerState.Faulted, "logon cancelled");
            throw;
        }

        if (config.AdvertiseActive)
            await SetAdvertiseActiveAsync(true).ConfigureAwait(false);
    }

    /// <summary>Logs off (if logged on), shuts the Steam interface down and stops the pump thread. Idempotent.</summary>
    public async Task StopAsync()
    {
        if (State is GameServerState.Created or GameServerState.Stopped)
            return;
        SetState(GameServerState.LoggingOff, "stopping");

        if (_loggedOn && _api is not null)
        {
            var logoffTcs = _logoffTcs; // captured before queuing: don't race a restart clearing the field
            _ = Enqueue("LogOff", () => _api.LogOff(_gs));
            // Don't hang teardown on Steam's ack.
            await Task.WhenAny(logoffTcs!.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        _stopRequested = true;
        _workSignal.Release();
        var pump = _pumpThread;
        if (pump is not null)
        {
            // The pump drains queued work (the queued SteamGameServer_Shutdown included) before exiting.
            if (!pump.Join(TimeSpan.FromSeconds(5)))
                ReportError(new SteamApiException(SteamResult.Timeout, "pump thread did not stop within 5s"));
            _pumpThread = null;
        }

        _api?.Dispose();
        SetState(GameServerState.Stopped, "stopped");
    }

    /// <summary>
    /// steamclient resolves the appid from its launch environment (setenv after process start is not
    /// reliably seen), the cwd, or steam_appid.txt next to the executable — it does NOT probe the
    /// native library's directory. Cover env vars plus both txt locations.
    /// </summary>
    private static void PrepareAppIdEnvironment(GameServerConfiguration config)
    {
        var appIdText = config.AppId.ToString(CultureInfo.InvariantCulture);
        SetenvIfUnset("SteamGameServerAppId", appIdText);
        SetenvIfUnset("SteamAppId", appIdText);
        TryWriteAppIdTxt("steam_appid.txt", appIdText);
        TryWriteAppIdTxt(Path.Combine(AppContext.BaseDirectory, "steam_appid.txt"), appIdText);
        if (!string.IsNullOrEmpty(config.AppToken))
            Environment.SetEnvironmentVariable("STEAMAPPTOKEN", config.AppToken);
    }

    /// <summary>Runs on the pump thread: init, interfaces, settings, warning hook, logon.</summary>
    private void Bootstrap()
    {
        var cfg = _config!;
        var api = _api!;

        var err = Marshal.AllocHGlobal(SteamNativeApi.MaxSteamErrMsg);
        try
        {
            // Version string + interface-version blob are short statics → stack buffers (Utf8).
            int initResult = Utf8.Eval2(cfg.VersionString, SteamNativeApi.InterfaceVersionBlob,
                (Api: api, Cfg: cfg, Err: err),
                static (s, version, blob) => s.Api.GameServerInitV2(s.Cfg.BindIp, s.Cfg.GamePort, s.Cfg.QueryPort,
                    (int)s.Cfg.Mode, version, blob, s.Err));
            if (initResult != 0)
            {
                var detail = Marshal.PtrToStringUTF8(err);
                throw new SteamNativeException(
                    $"SteamGameServer_Init failed ({initResult} " +
                    $"{(initResult is 1 or 2 or 3 ? (initResult == 1 ? "FailedGeneric" : initResult == 2 ? "NoSteamClient (is Steam running?)" : "VersionMismatch (bundled steam_api vs running steamclient)") : "unknown")}): {detail}");
            }
            _nativeInitialized = true;
        }
        finally
        {
            Marshal.FreeHGlobal(err);
        }

        api.ManualDispatchInit();
        _pipe = api.GetHSteamPipe();
        _gs = api.GetSteamGameServer();
        _stats = api.GetSteamGameServerStats();
        _utils = api.GetSteamGameServerUtils();
        if (_gs == IntPtr.Zero || _stats == IntPtr.Zero || _pipe == 0)
            throw new SteamNativeException("Interface accessors returned null — steamclient rejected the interface versions.");

        // Valve's example order: identity (pre-logon only), then browser metadata.
        ApplySettings(cfg);

        // Route steamclient warnings into the sink; keep the delegate rooted in a field.
        _warningHook = OnSteamWarning;
        api.SetWarningMessageHook(_utils, Marshal.GetFunctionPointerForDelegate(_warningHook));

        if (string.IsNullOrEmpty(cfg.LoginToken))
            api.LogOnAnonymous(_gs);
        else
            Utf8.Use(cfg.LoginToken, (Api: api, Gs: _gs), static (s, token) => s.Api.LogOn(s.Gs, token));

        SetState(GameServerState.WaitingForLogon, "waiting for logon");
        _initTcs!.TrySetResult(true);
    }

    private void OnSteamWarning(int msgType, IntPtr message)
    {
        try
        {
            _events?.OnSteamWarning(msgType, Marshal.PtrToStringUTF8(message) ?? "");
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private static void SetenvIfUnset(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static void TryWriteAppIdTxt(string path, string appIdText)
    {
        try
        {
            if (!File.Exists(path))
                File.WriteAllText(path, appIdText);
        }
        catch
        {
            // read-only location: env vars remain as the fallback
        }
    }
}
