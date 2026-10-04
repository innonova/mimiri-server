# Mimiri Server

The sync relay behind [Mimiri Notes](https://mimiri.io): two ASP.NET
Core services over one Postgres database. The server stores and
relays encrypted notes and keys; it never holds a user's password, a
plaintext note or a key that can open one. Client: `mimiri-client`
(the web bundle), whose `docs/architecture.md` describes the other side
of every request below. Project map: `mimiri-project/README.md`.

## The solution

| Project | What it is |
|---|---|
| `Mimer.Notes.WebApi` | The REST service (`api.mimiri.io`): users, keys, notes, sync, sharing, notifications, feedback, admin. |
| `Mimer.Notes.SignalR` | The push service (`ws.mimiri.io`): one hub, `/notifications`, that tells clients to sync, to check for a bundle update, or that a blog post is out. |
| `Mimer.Notes.Server` | The domain: `MimerServer` (partial class, one file per concern) and `PostgresDataSource` (same, one file per table group). Both services reference it; only the WebApi instantiates `MimerServer`. |
| `Mimer.Notes.Model` | Request and response types, the crypto primitives shared with the client (`CryptSignature`, `SymmetricCrypt`, `PasswordHasher`, `KeySet`), and the data types. |
| `Mimer.Framework` | A small JSON library (`JsonObject`, `JsonTokenizer`; not System.Text.Json), byte IO, and `Dev.Log`. |

.NET 10, Npgsql, Swashbuckle (Swagger UI in Development only). No
ORM: every query is SQL in `PostgresDataSource.*.cs`, and the schema
is created idempotently at startup (`CreateDatabase`, `CREATE TABLE IF
NOT EXISTS`). There are no migrations: a schema change is an `IF NOT
EXISTS` or an `ALTER` guarded the same way.

## The security model, in short

- **Accounts.** `GET /api/user/pre-login/{username}` returns the
  password salt, iterations, algorithm and a one-time challenge (a fake
  but plausible set for unknown usernames, so names cannot be probed).
  `POST /api/user/login` answers the challenge with an HMAC of the
  client-derived password hash; the hash itself never travels. The
  response carries the user's wrapped private key, wrapped symmetric
  key and encrypted user data, which only the password can open.
  Creating an account needs a proof of work (15 bits, checked in
  `UsernameAvailable`) and a username outside `InvalidUsernames`.
- **Every authenticated request is signed.** The JSON body carries
  `signatures[]`: an RSA signature by the user's key over the body with
  `signatures` stripped (`CryptSignature.VerifySignature("user", …)`),
  a `TOKEN` entry checked against the stored password token for
  bundles 2.5.0 and up, and for key-scoped operations a signature by
  that key set. Non-repeatable requests also go through
  `RequestValidator`: a request id seen before, or a timestamp older
  than 20 minutes, is rejected.
- **The server's own key.** `CertPath` holds `server.key` and
  `server.pub` (RSA 3072). The WebApi signs with it as `"mimer"`: the
  notification token a client presents to the SignalR hub, and the
  server-to-server requests the WebApi posts to the SignalR service's
  `POST /api/notification/send`. The SignalR service only has the
  public half and verifies both. The client has the public key
  compiled in and encrypts account-creation bodies to it.
- **At rest.** User rows are encrypted with `AesKey` from
  configuration; notes and keys arrive encrypted by the client and are
  stored as received.

## Sync

`POST /api/sync/changes-since` returns the notes, keys and deletions
after the client's watermarks (`sync_sequence`, one sequence for the
database, assigned by trigger), with a sha256 over the response and
the user's size and count against the limits of their user type.
`POST /api/sync/push-changes` applies a batch of note and key actions
inside a per-user writer lock (`MimerServer.SyncLock.cs`; readers
share), checks the limits (`PostgresDataSource.Limits.cs`), and then
notifies every user who owns one of the touched keys through SignalR
(`NotifySync`, type `sync`, payload the client's `syncId` so the
sender can ignore its own echo). The single-note endpoints under
`/api/note` predate sync and remain for sharing and older clients.

## Routes

| Controller | Route | Endpoints |
|---|---|---|
| `UserController` | `api/user` | `create`, `update`, `update-data`, `pre-login/{username}` (GET), `login`, `get-data`, `public-key`, `delete`, `available` |
| `KeyController` | `api/key` | `create`, `read-all`, `read`, `delete` |
| `NoteController` | `api/note` | `multi`, `create`, `read`, `update`, `delete`, `share`, `share-offers`, `share-offer`, `share/delete`, `share-participants` |
| `SyncController` | `api/sync` | `changes-since`, `push-changes` |
| `NotificationController` | `api/notification` | `create-url` (mints the hub URL and a signed token), `notify-update` (GET; broadcasts `bundle-update`), `send` (a no-op here; the real one is on the SignalR service) |
| `FeebackController` (sic) | `api/feedback` | `add-comment` |
| `AdminController` | `api/admin` | `promote-user` |
| `HealthController` | `api/health` | `error` |

All POST bodies are `JsonObject` via `JsonModelBinder`; responses are
the model's JSON as `text/plain`. `null` from `MimerServer` becomes
404 or 409 in the controller. Every request records an action with the
client's `User-Agent` and `X-Mimiri-Version` (`ClientInfo`, the stats
managers), which is how per-version behaviour is gated.

The SignalR service: `POST /api/notification/send` (server-signed;
`bundle-update` and `blog-post` broadcast to all, everything else to
the listed user ids), `GET /api/health`, and the hub at
`/notifications` authenticated by the bearer token from `create-url`
(`MimerAuthenticationHandler`).

## Configuration

Standard ASP.NET Core configuration: `appsettings.json` plus the
environment. The keys the code reads:

| Key | Service | Meaning |
|---|---|---|
| `ConnectionStrings:Default` | WebApi | Npgsql connection string |
| `AesKey` | WebApi | base64 key for user rows at rest |
| `CertPath` | both | directory with `server.key` (WebApi) and `server.pub` (both) |
| `LogPath` | both | file for `Dev.Log` |
| `WebsocketUrl` | WebApi | the public SignalR URL put into notification tokens (`https://ws.mimiri.io`) |
| `NotificationsUrl` | WebApi | where the WebApi reaches the SignalR service's REST side (private address on the same host) |
| `AllowedOrigins` | both | CORS list (`AllowedOrigins__0`, `__1`, … as environment variables) |

In the estate these come from the service declaration (`services/
mimiri-webapi.yml`, `mimiri-signalr.yml` in `innonova/iac`, plain
values per environment) and from the secret store (`kv/<env>/<name>`)
for the connection string, the AES key and the key files. Never commit
or print a filled configuration.

## Build, run, release

```sh
dotnet build Mimer.Notes.Rest.sln
dotnet run --project Mimer.Notes.WebApi      # needs Postgres and a configuration
dotnet run --project Mimer.Notes.SignalR
```

A local run creates the schema in whatever database the connection
string names. There is no test project.

Release: tag `vX.Y.Z` on `main`. `.github/workflows/release.yml`
publishes both services for linux-x64 (framework-dependent, the hosts
have the ASP.NET Core 10 runtime), tars each, signs with cosign and
creates a GitHub release. Deploying is a tag bump in the estate's
`environments/<env>/versions.yml` (`mimiri-webapi`, `mimiri-signalr`),
dev first. `build.webapi.sh` and `build.signalr.sh` are the older
folder-publish used before the estate.

## Contributing

Issues and discussion: [Discord](https://discord.gg/pg69qPAVZR).
Changes land by pull request to `main`. Style is the existing code:
tabs, braces on the same line, `Dev.Log` for logging.
