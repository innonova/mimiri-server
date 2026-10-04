# CLAUDE.md — mimiri-server

Read `README.md` first: what the two services are, the security model,
sync, the route table, configuration, release. This file is what an
agent gets wrong without being told.

## Where things are

- `MimerServer` is a partial class, one file per concern under
  `Mimer.Notes.Server/MimerServer/` (`Auth`, `Sync`, `Notes`, `Keys`,
  `Sharing`, `Notifications`, `Comments`, `Admin`, `SyncLock`,
  `Utilities`). `PostgresDataSource` likewise under `PostgresDataSource/`.
  Add to the file that owns the concern, not a new one.
- Request and response shapes are classes over `JsonObject` in
  `Mimer.Notes.Model/Requests` and `Responses`. The client's
  counterparts are in `mimiri-client/src/services/storage/mimiri-client.ts`
  and `types/`. A shape change is a change on both sides and must stay
  readable by older bundles: gate new behaviour on
  `ClientInfo.IsBundleVersionGreaterThanOrEqualTo(...)` as the token
  check does.
- The crypto primitives in `Mimer.Notes.Model/Cryptography` mirror
  `mimiri-client/src/services/crypt-signature.ts` and
  `symmetric-crypt.ts` byte for byte (RSASSA-PKCS1-v1_5/SHA-256 over
  the JSON with `signatures` removed; AES-CBC-PKCS7 for the dotnet
  compatible path). Do not change one without the other.
- Logging is `Dev.Log(...)` to `LogPath`; there is no ILogger wiring.

## Things that are easy to get wrong

- **Three file names contain a space** (`DbNote .cs`,
  `GlobalStatsManager .cs`, `UpdateUserDataRequest .cs`) and one
  controller is misspelled (`FeebackController`). They compile and are
  referenced by name nowhere, but a rename is a visible change; do it
  deliberately or not at all.
- **The schema is created at startup, idempotently.** A new column or
  table is a guarded statement in the matching `Create…Tables`; a local
  run against the wrong connection string creates the schema there.
- **`POST /api/notification/send` on the WebApi does nothing.** The one
  the WebApi calls is on the SignalR service at `NotificationsUrl`.
  `GET /api/notification/notify-update` on the WebApi is unauthenticated
  and broadcasts `bundle-update` to every connected client; it is how
  a new canary is announced.
- **Limits come from `user_types`** (max bytes, note bytes, note count,
  history entries) and are enforced on push, not on the single-note
  endpoints alone; `SYSTEM_NOTE_COUNT` (3) is excluded from the count.
- **Static configuration.** `Program.cs` sets static properties on
  `MimerServer` before DI builds it; a test or a second host must do
  the same.
- `appsettings.Development.json` is stripped from release tarballs by
  the workflow; never put real values in a committed file, and never
  print a filled configuration or an env file (secrets go nowhere but
  the secret store).
- Deploys are a tag plus a version bump in `innonova/iac`; the
  `build.*.sh` scripts and anything that scp's to a host by IP are the
  retired path.

## Verifying

There is no test project. `dotnet build Mimer.Notes.Rest.sln` is the
gate; behaviour is verified against a local Postgres, or on dev
(`dev-api.mimiri.io` after a tag and a dev version bump), with the
client's Playwright suite (`mimiri-client/docs/testing.md`) or
`mimiri-e2e`'s staging-sync test as the end-to-end check.
