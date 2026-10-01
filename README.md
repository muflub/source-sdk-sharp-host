# SourceSharp Host

[![linux](https://github.com/muflub/source-sdk-sharp-host/actions/workflows/linux.yml/badge.svg)](https://github.com/muflub/source-sdk-sharp-host/actions/workflows/linux.yml)

The hosting service for **Descent**, a game built on
[SourceSharp](https://github.com/muflub/source-sdk-sharp). Everything that runs *beside*
the game lives here:

- **the UDP gateway**: one public port (`27015/udp`) relaying players to the right game pod
- **the instance manager**: runs Source dedicated servers as Kubernetes pods (hub and levels)
- **the database**: characters, progress, and an item ledger where the service mints every item
- **the map pool**: levels generated and baked ahead of time from `ssmap` rooms
- **the gRPC API and C# SDK** game servers use (`SourceSharp.Host.Sdk`, `SourceSharp.Host.Contracts`)
- **the admin UI**: Blazor Server, bound to loopback only

All of it is managed C# on ASP.NET Core (.NET 10). The design, its rulings and its phases
are in [`docs/plans/plan_host.md`](docs/plans/plan_host.md).

```
 players ──UDP 27015──▶ gateway ──relayed UDP──▶ hub pod / level pods (srcds + the game)
                           ▲                              │ gRPC (Host.Sdk)
                           └──── gRPC control ──── service (state, workers, admin, map pool)
```

## Layout

| path | what |
|---|---|
| `src/Host.*` | generic hosting: `Abstractions`, `Proto`, `Contracts`, `Sdk`, `Modules`, `Gateway`, `Instances`, `MapPool`, `Data`, `Admin`, `Relay`, `Launcher`, `FakeGame`, `FakeClient`, `Local` |
| `src/Descent.*` | game-specific: `Service` (the composition root and main executable) and `MapForge` |
| `src/*.Tests` | xUnit tests, one project per library |
| `proto/` | the gRPC contract |
| `deploy/` | Dockerfiles (`service`, `gateway`, `engine`, `fake`), Kubernetes manifests, helper scripts |
| `live/` | live gates against a local cluster (`TIER=fake` or `TIER=real`) |
| `docs/` | the plan, [`net-protocol.md`](docs/net-protocol.md), [`mod-image.md`](docs/mod-image.md), [`ops.md`](docs/ops.md), [`sdk-integration.md`](docs/sdk-integration.md) |

## Building

Requirements:

- Linux x64 with the .NET SDK pinned in [`global.json`](global.json) (10.0.112, latest patch).
  The Makefile uses `~/.dotnet/dotnet`; pass `DOTNET=dotnet` to use another install.
- `git` and `make`.
- `clang` and `zlib1g-dev` only for `make publish` (`Host.Launcher` is NativeAOT).
- Read access to [`source-sdk-map-tools`](https://github.com/muflub/hl2sdk_tools_sharp), the one
  build-time dependency, referenced as a library.

```sh
make setup                # clone ../source-sdk-map-tools if missing, write roots.props, restore
make build                # Debug build into bin/Debug/<project>/
make test                 # all xUnit tests
make check                # build + test, before every commit
make publish              # linux-x64 publish of the executables, tarballs in bin/dist/
make pack                 # Host.Sdk + Host.Contracts NuGet packages into bin/nupkg/
```

`make setup MAPTOOLS_ROOT=/path/to/map-tools` uses an existing checkout instead. A missing
`roots.props` fails the build with "run `make setup`". Build output and intermediates go to
`bin/` (gitignored); nothing is ever written into the map tools checkout.

To run tests by hand, disable MSBuild node reuse (reused nodes hang):

```sh
MSBUILDDISABLENODEREUSE=1 ~/.dotnet/dotnet test Host.slnx --nologo -m:1
```

## The Linux build

`make publish` produces one tarball per executable in `bin/dist/`, plus `SHA256SUMS`:

| tarball | what | needs at run time |
|---|---|---|
| `Descent.Service-<version>-linux-x64.tar.gz` | the service: gRPC APIs, workers, map pool, admin | ASP.NET Core 10 runtime |
| `Host.Gateway-<version>-linux-x64.tar.gz` | the UDP gateway | ASP.NET Core 10 runtime |
| `Host.FakeGame-<version>-linux-x64.tar.gz` | the fake game server used by tests and `TIER=fake` | ASP.NET Core 10 runtime |
| `Host.Launcher-<version>-linux-x64.tar.gz` | the game pod's launcher (NativeAOT) | nothing (native executable) |

### Container images

CI also builds the container images from `deploy/Dockerfile.*` and pushes them to the GitHub
Container Registry:

| image | from |
|---|---|
| `ghcr.io/muflub/descent-service` | `deploy/Dockerfile.service` |
| `ghcr.io/muflub/descent-gateway` | `deploy/Dockerfile.gateway` |
| `ghcr.io/muflub/descent-fake` | `deploy/Dockerfile.fake` (the engine image of `TIER=fake`) |

A push to `main` tags them `main` and `sha-<short sha>`; a `v1.2.3` tag adds `1.2.3` and
`latest`. Pull requests build the images without pushing. The engine image is not built in CI:
it needs engine files that only a licensed install provides (`make image-engine`, `docs/ops.md`).

## Running

```sh
make run                  # Descent.Service as a local process
make local                # the service in local mode; the game connects with -hostlocal bin/local/local-host.json
make gateway              # Host.Gateway
make fake                 # the fake game server
```

### On a local cluster

```sh
make images                                   # build and push service, gateway, engine, fake images
make cluster-up MOD_IMAGE=<registry>/<name>:<tag>
make deploy [TIER=fake|real]
make admin                                    # port-forward the loopback-only admin UI to :5000
make live-h4 TIER=fake                        # a live gate
make undeploy && make cluster-down
```

Live gates only ever run against a local cluster (`deploy/cluster-up.sh` refuses any other API
server). The game's built files arrive as the **mod image**, named by runtime config; its
contract is [`docs/mod-image.md`](docs/mod-image.md). Operational notes and measurements are in
[`docs/ops.md`](docs/ops.md).

## Integrating the game

Game servers talk to the service only through `SourceSharp.Host.Sdk`. The guide is
[`docs/sdk-integration.md`](docs/sdk-integration.md); the working reference is
`src/Host.FakeGame/FakeGameServer.cs`, a game server written only against the SDK.

## CI

[`.github/workflows/linux.yml`](.github/workflows/linux.yml) runs on every push to `main`, every
pull request, and by hand:

1. checks out this repo and the map tools, installs the SDK from `global.json`
2. `make setup`, `make build CONFIG=Release`, the tests (results uploaded as `test-results`)
3. `make publish` and `make pack`, uploaded as the workflow artifact **`linux-build`**
4. once the tests pass, the service, gateway and fake images, pushed to `ghcr.io` on `main` and
   on tags (see [Container images](#container-images))

Pushing a tag `v*` (for example `v0.1.0`) does the same and then creates a GitHub Release for the
tag with the tarballs, `SHA256SUMS` and the NuGet packages attached, the packages versioned from
the tag. A tag with a `-` suffix (`v0.2.0-rc1`) is marked as a prerelease.

The map tools are a private repository, so CI needs one repository secret:

| setting | kind | value |
|---|---|---|
| `MAPTOOLS_TOKEN` | secret | a fine-grained token with read-only **Contents** access to `muflub/hl2sdk_tools_sharp` |
| `MAPTOOLS_REF` | variable, optional | the map tools ref to build against (default `main`) |

Images push with the workflow's own `GITHUB_TOKEN`; no registry secret is needed. The first
push creates each package as private: make it public, or link it to this repository, under the
package's settings on GitHub.

## Contributing

Read [`CLAUDE.md`](CLAUDE.md): it holds the repo's rules (one behaviour per `[Fact]`, a regression
test with every fix, no native interop, `make check` before a commit) and the reason for each.

## License

[MIT](LICENSE)
