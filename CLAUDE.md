# SourceSharp Host: rules for Claude Code sessions

This repo is the hosting service for a SourceSharp game: the UDP gateway, the
manager that runs dedicated servers as Kubernetes pods, the character / item /
progress database, the map pool built on `ssmap` rooms, the gRPC API and C# SDK
game servers use, and the admin UI. All C# on ASP.NET Core. The design is
`docs/plans/plan_host.md`; read its rulings table (§0) before changing a
boundary. Each rule below carries its reason: read the reason before you work
around a rule.

## Core rules

- **Commit after every step.** One reviewable unit per commit. A replacement
  and the deletion of the thing it replaced are separate commits.
- **Test all logic with xUnit `[Fact]`s, one behaviour per fact.** Never the
  `Run(Action<string,bool>)` shape: it compiles and xUnit never runs it.
- **The build must compile and produce correct output after every change.**
  Zero warnings before a phase is called done.
- **Everything is managed C#.** No `DllImport`, `LibraryImport`, `extern` or
  `AllowUnsafeBlocks` anywhere, and `Host.Sdk` / `Host.Contracts` reference no
  engine assembly: they ship into the game, which is managed-only by its own
  rule (TF2 plan R15). A fact reads each project file and refuses these.
- **Work autonomously through a plan.** Don't stop between phases or ask
  "should I continue?". Stop only for ambiguous requirements that change what
  gets built, destructive or outward-facing actions (push, publish, delete,
  anything that changes a cluster that is not `k3d`), and checkpoints the plan
  names.
- **Keep output small.** Pipe builds, greps and `kubectl` output through
  `head` / `wc -l`, or write them to a file and read the failures. Keep state
  that must survive compaction in a scratch file on disk.
- **Plans live in `docs/plans/` and stay uncommitted until the user has
  reviewed them.**
- New lessons go in this project's Claude memory directory.

## What this repo depends on, and what it never touches

| input | how it arrives | rule |
|---|---|---|
| `source-sdk-map-tools` (`ssmap` as a **library**: `SourceSharp.MapTools`, `MapFormats`, `RoomContracts`) | sibling checkout or clone, `make setup` → `roots.props` (`$(MapToolsRoot)`) | the only build-time root. Never run `ssmap` as a process. After any build, `git -C $(MapToolsRoot) status --porcelain` is empty: we never write into a root |
| the game (`source-sdk-sharp`) | **not a build root.** Its built files arrive as the **mod image** named by runtime config (`Instances.ModImage`); the rules module the host replays with arrives from the game server's SDK at first connect, hash first, bytes only on request (plan D-H9) | nothing here compiles against a Descent assembly; tests use `FakeGameRules`. `SOURCE_SHARP_ROOT` exists only for `make mod-image-dev` |
| the engine | `srcds_linux64` from Steam app 244310 by anonymous `steamcmd` into the **engine image** | never `source#` in a pod (it needs a Steam client). TF2 content is a volume filled by a Job, never in an image built here, never in this repo |
| the Valve fork, TF2 assets | — | never referenced, never copied |

- **The mod image is an input, not a build.** Its contract is
  `docs/mod-image.md`; the digest each instance actually ran is recorded on
  its row. Don't bake it into an image: it is runtime config so it can change
  without a rebuild here.
- **Descent-specific logic goes in `Descent.Service`; generic in `Host.*`.**
  The tie-break is ruled (plan Q2): when keeping a thing generic would cost the
  game an abstraction, an interface or a round-trip, it goes in
  `Descent.Service` and the split is not widened.

## Layout and build

| Path | What |
|---|---|
| `docs/plans/` | the plans; `docs/net-protocol.md`, `docs/mod-image.md`, `docs/ops.md` are the notes the plan's gates produce |
| `src/Host.*` | generic: `Abstractions`, `Proto`, `Contracts`, `Sdk`, `Modules`, `Gateway`, `Instances`, `MapPool`, `Data`, `Admin`, `FakeGame`, `FakeClient` |
| `src/Descent.*` | game-specific: `Service` (the composition root and exe), `MapForge` |
| `src/*.Tests` | xUnit, one project per lane's project |
| `live/` | live gates as scripts, `TIER=fake` (default) or `TIER=real` |
| `deploy/` | Dockerfiles (`service`, `gateway`, `engine`, `fake`), `k8s/base`, `k8s/k3d` |
| `bin/` | gitignored: build output and `bin/obj/` intermediates, `bin/nupkg/` from `make pack` |
| `roots.props` | gitignored, written by `make setup`; a missing file fails the build with "run `make setup`" |

```
make setup [MAPTOOLS_ROOT=..]      make build | make test | make check   (build + test, before a commit)
make images                        make cluster-up MOD_IMAGE=… deploy | make undeploy | make cluster-down
make admin                         port-forward the loopback-only admin UI
make live-h4 … live-h9 [TIER=fake|real]
make pack                          Host.Sdk + Host.Contracts as NuGet packages into bin/nupkg
```

- `Directory.Build.props` mirrors the sharp repo's: `net10.0`, nullable,
  deterministic, CS0108 / CS0114 as errors and **only** those. Not
  `TreatWarningsAsErrors`: that lets an SDK update stop the build for reasons
  unrelated to the code. Use `~/.dotnet/dotnet`, never Ubuntu's `/usr/bin/dotnet`
  (its runtime aborts under concurrent tiered compilation).
- **A managed change is deployed by an image build and a rollout**, not by
  `dotnet build`. A green test run says nothing about what a cluster runs;
  confirm with the version on `/healthz` or the Dashboard.
- Proto is the contract. `Host.Proto` never learns a stat name: sheets and
  items are `bytes` + `schema_version`, decoded by the rules module's own
  codecs on both sides. A breaking proto change bumps the SDK and contract
  versions together.
- The admin endpoint binds loopback only and `ValidateOnStart` refuses any
  other address; the public surface is UDP `27015` and the fastdl listener
  that serves `*.bsp.bz2` and nothing else. `.nav3d` and `.map2d` never leave
  the cluster (the automap is revealed by the server, room by room).

## Tests and mutation proofs

- Run managed tests as
  `MSBUILDDISABLENODEREUSE=1 ~/.dotnet/dotnet test --nologo -m:1`. Reused
  MSBuild nodes hang. Judge a run by its `Failed: N` summary: `[FAIL]` lines
  appear only on a TTY.
- **Three tiers, cheapest first.** (1) facts with fakes: `FakeInstanceHost`
  (an in-memory pod store), `FakeGameHost` (the fake game server hosted
  in-process, written on `Host.Sdk`), `Host.FakeClient`, `FakeMapCompiler`,
  `FakeGameRules`, SQLite `:memory:` for the real stores, a fake clock;
  (2) `TIER=fake` live gates on `k3d` with the fake image as the engine image;
  (3) `TIER=real` with the engine. Write the fact first; a live gate proves the
  same thing in the environment it will run in, it never replaces the fact.
- **The fake game server is the SDK's proof.** It may reference `Host.Sdk`
  and never `Host.Proto` directly (a fact checks). A feature the fake cannot
  express through the SDK is a gap in the SDK, found before the game needs it.
- **Ledger facts count.** Every item path asserts count-in = count-out
  (mint → claim → drop → death → reap → Lost & Found → reclaim), and the
  property test over random RPC sequences with mid-sequence reaps stays in
  the suite. An item with two owners or none is a bug, never a tolerance.
- Every bug fix lands with a regression test that fails before the fix.
- **A check that cannot fail is not evidence.**
  - Break the subject and see red.
  - A missing, stale or unreadable input is a failure, never a pass.
  - Prove the setup took effect: before ≠ after; a refusal test first shows
    the unpatched input is accepted (a wrong-hash module, a wrong-mod pack,
    a stale lease token, a forged reveal).
  - Anchor needles on whole constructs; verify "X did not change" by diffing
    extracted values, not by a negative grep.
  - Make sure the stimulus reaches the subject: require a non-zero in the
    positive arm (a fake backend's receive log, a sweep's audit counts).
- **Exit statuses die in wrappers** (`| tail`, `; echo`). Use
  `cmd > log 2>&1; rc=$?` or `pipefail`. Stamp logs another check will read
  with `TIP=$(git rev-parse HEAD)` and the exit code.
- **A count cannot tell restored from rebuilt.** Gate persistence (adoption
  after a service restart, the gateway's `SyncTable`, the pool surviving a
  restart) on a differential with a negative control.
- **Mutation harnesses:** snapshot with `cp`, restore from the snapshot in a
  `finally` / `trap`, never with `git checkout --` (it deleted uncommitted
  work); restore with a fresh mtime or MSBuild skips the rebuild; hash before
  and after to prove the mutation landed. Committed harnesses find their root
  from their own location, never from an absolute worktree path.

## Live gates, the cluster and processes

- Live gates run against `k3d` (`make cluster-up`), never against a cluster
  someone else is using. Anything that mutates a non-`k3d` context is an
  outward-facing action: stop and ask.
- **Pods are addressed by name and uid**, processes by PID after checking
  `ps -o args=`. Never `pkill -f` or a class-wide pattern (it has killed the
  user's proxy and its own wrapper). A pod with the same name and a different
  uid is a different pod: treat it as `crashed`, never adopt it.
- **The bound port is what the pod reports, not what was asked.** Each pod
  has its own IP so `27015` should never walk, and `MapReady {port}` is still
  compared with what the socket reports, because an engine that silently binds
  a higher port has cost a lane a day before.
- Concurrent local game processes (the `LocalProcessInstanceHost` dev path)
  each need their own `TMPDIR`, `SOURCESHARP_DEVAPI_DIR` and `-port`. With
  several up, Steam auth tickets fail and the client never joins: assert the
  join before trusting any client reading.
- `kubectl delete pod` in a gate is the crash stimulus; the assertion is the
  row's transition, the sweep's audit counts and the released leases, not the
  pod's absence.
- **Intermittent faults need numbers:** trials, regime and P(0 | old rate).
  Attribute crashes by commit and image digest, not by worktree path.
- Never wrap a long-running gate in `timeout` with a pattern kill; give it its
  own process tree and stop it by PID.

## Git, worktrees and lanes

- Multi-phase or delegated work happens in a dedicated `git worktree`. Commit
  or stash before any merge: shared branches are force-updated, and staged
  work gets absorbed into peers' merges. Never `git add -A`; stage explicit
  paths. Never push.
- A branch checked out in a peer's worktree can't be moved from here: merge
  their tip, verify, and hand over `git merge --ff-only <branch>` with the
  counts.
- **Delegate by project ownership** (the plan's lanes: G gateway, I instances,
  M MapForge + pool, D data + ledger + API + SDK, A admin), and serialise the
  proto and `Host.Contracts`: they are the contract every lane builds against.
  Relay agents' conclusions: the user does not see them.
- **Worktree-agent briefs:** name the base sha and have the agent check it
  first (`git log --oneline -1`). Give it its own `make setup` and its own
  `k3d` cluster name, commit after each step, prefix its scratch files.
  Agent worktrees are cut from the default branch, not yours.
- **A brief's premises are load-bearing:** cite facts, quote numbers with their
  commit, and have lanes re-measure at the current tip. Let "no code needed"
  be an acceptable result.
- **A lane's evidence is true only at its base.** Re-measure at the merge, and
  never resolve a conflict in a measured file by picking a side.

## Verify before claiming

- Back every claim about build output, images, cluster state or generated
  files with a command you ran this session, and show the command.
- Confirm the working directory and the `kubectl` context, and force a cold
  rebuild when output looks stale.
- Re-verify quoted numbers with a second, independent command. Numbers that
  reconcile arithmetically (mints − terminal events = live items) are worth
  more than numbers that look right.
- Every number written into a plan is backed by a command run in that session
  and a second one; the plan's measurement tables carry the command.
