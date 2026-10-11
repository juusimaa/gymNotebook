# deploy/: production on UpCloud

Gym Notebook runs on the Subscription Tracker's UpCloud server (`fi-hel1`),
beside the tracker and isolated from it: its own containers with hard memory
and CPU caps, its own database and role in the shared managed PostgreSQL 18
service, its own secrets file and its own deploy key. TLS and routing are the
tracker's Caddy, which imports our site file. The design is
[specs/005-upcloud-migration/](../specs/005-upcloud-migration/); this file is
how to run it.

Until the cutover, Azure + Neon is still production and the server serves
only `api.gymnotebook.fit` (rehearsal). § Cutover moves `gymnotebook.fit`.

| File | On the server | What it is |
|---|---|---|
| `compose.prod.yml` | `/opt/gymnotebook/compose.prod.yml` | the two app containers, `gym-api` and `gym-web` |
| `gymnotebook.caddy` | `/opt/edge/sites/gymnotebook.caddy` | our site blocks, imported by the tracker's Caddyfile |
| `bin/deploy.sh` | `/opt/gymnotebook/bin/deploy.sh` | the deploy key's forced command |
| `bin/verify-copy.sql` | `/opt/gymnotebook/bin/verify-copy.sql` | row counts per table, to compare two databases |
| `tests/` | — | CI only: fake env file, Caddy wrapper, deploy guard test (`deploy-config` job in `.github/workflows/test.yml`) |

## Server layout

| Path | Owner, mode | Written by | Holds |
|---|---|---|---|
| `/opt/gymnotebook/` | `root:root 0755` | admin, from this directory | `compose.prod.yml`, `bin/` |
| `/opt/edge/sites/gymnotebook.caddy` | `root:root 0644` | admin | our site blocks |
| `/etc/gymnotebook/.env` | `root:gymdeploy 0640` | admin, with `sudo -e` | every variable in § Environment |
| `/etc/gymnotebook/upcloud-ca.pem` | `root:gymdeploy 0640` | admin | the database service's CA, mounted read-only into `gym-api` |
| `/var/lib/gymnotebook/image-tag.env` | `gymdeploy` | `deploy.sh` only | `IMAGE_TAG=main-<40 hex>` |
| `/var/lib/gymnotebook/previous-tag` | `gymdeploy` | `deploy.sh` only | rollback target |

| User | Groups | Login |
|---|---|---|
| `gymdeploy` | `gymdeploy`, `docker` | key only, forced command (§ Deploy) |
| `admin` (tracker's) | also `gymdeploy`, to read our env file | unchanged |
| `deploy` (tracker's) | **not** `gymdeploy` | unchanged |

### Running Compose by hand

The `admin` login shell sets `COMPOSE_FILE` and `COMPOSE_ENV_FILES` to the
**tracker's** stack, so a bare `docker compose …` there acts on the tracker.
For Gym Notebook always name the project, file and env files. Define this in
the session first; every command below uses it:

```sh
gn() {
  docker compose -p gymnotebook -f /opt/gymnotebook/compose.prod.yml \
    --env-file /etc/gymnotebook/.env \
    --env-file /var/lib/gymnotebook/image-tag.env "$@"
}
```

`gn ps`, `gn logs -f gym-api`, `gn up -d --wait` then only ever touch
`gymnotebook-gym-api-1` and `gymnotebook-gym-web-1`.

## Environment

`/etc/gymnotebook/.env` is the only place app secrets exist in production
(contract: [server-env.md](../specs/005-upcloud-migration/contracts/server-env.md)).
GitHub holds only the deploy key, host and known-hosts line. Edit with
`sudo -e /etc/gymnotebook/.env`, apply with `gn up -d --wait`. Never copy a
value from a laptop `.env`, and never paste one into a PR, issue or chat.

| Variable | Where the value comes from |
|---|---|
| `ConnectionStrings__Default` | the `gymnotebook` role from § Provision: `Host=<private host>;Port=<port>;Database=gymnotebook;Username=gymnotebook;Password=<pw>;SSL Mode=VerifyFull;Root Certificate=/etc/gymnotebook/upcloud-ca.pem;Maximum Pool Size=10` |
| `Jwt__Secret` | **from Azure**, unchanged, so signed-in sessions survive the move |
| `Jwt__ExpiryMinutes` | `30` |
| `CORS_ORIGINS` | `https://gymnotebook.fit` |
| `PRIVACY_LIFECYCLE_ENABLED` | `false` |
| `Email__Backend` | `resend` |
| `EMAIL_FROM` | `Gym Notebook <no-reply@mail.gymnotebook.fit>` |
| `APP_URL` | `https://gymnotebook.fit` |
| `RESEND_API_KEY` | **from Azure**, unchanged |
| `TURNSTILE_SECRET_KEY` | **from Azure**, unchanged |
| `TURNSTILE_HOSTNAMES` | `gymnotebook.fit` |
| `TURNSTILE_SITE_KEY` | the GitHub variable of the same name (public); read by `gym-web` |

`Maximum Pool Size=10` keeps Gym Notebook to 10 of the service's 50
connections (tracker ≤ 15). `FORWARDED_HEADERS_ENABLED` and `gym-web`'s
`API_URL` are set in `compose.prod.yml`, not here, because they are facts
about this stack rather than secrets.

## Provision

Sets Gym Notebook up beside the tracker, on an empty database. Preconditions:
the tracker PR that adds the shared edge is applied (its § Provision creates
the `edge` network and `/opt/edge/sites`, and its Caddyfile imports
`/etc/caddy/sites/*.caddy`), and the tracker connects with its own role, not
`upadmin`.

`<host>` and `<port>` are the database's **Connection → Private** values in
the UpCloud console (the port is not 5432).

1. **Database and role**, as `upadmin`, from the server, over the private
   network. The CA is the same file the tracker uses: one service, one CA.

   ```sh
   docker run --rm -it -v /etc/subscription-tracker/upcloud-ca.pem:/ca.pem:ro postgres:18 \
     psql "postgresql://upadmin@<host>:<port>/defaultdb?sslmode=verify-full&sslrootcert=/ca.pem"
   ```

   ```sql
   -- Login only: no superuser, CREATEDB or CREATEROLE. It owns its database
   -- so the app's migrations can create tables.
   CREATE ROLE gymnotebook LOGIN;
   \password gymnotebook
   CREATE DATABASE gymnotebook OWNER gymnotebook;
   -- New databases let every role connect (PUBLIC). Only ours may.
   REVOKE CONNECT ON DATABASE gymnotebook FROM PUBLIC;
   GRANT CONNECT ON DATABASE gymnotebook TO gymnotebook;
   ```

   `\password` prompts for the password, so it is never in the shell
   history. Generate it with `openssl rand -base64 33 | tr -d '/+='` and keep
   it in your password manager.

2. **Files.** From a checkout of `main`:

   ```sh
   rsync -r deploy/compose.prod.yml deploy/gymnotebook.caddy deploy/bin admin@<server>:/tmp/gn/
   ssh admin@<server>
   sudo install -d -o root -g root -m 0755 /opt/gymnotebook
   sudo cp -r /tmp/gn/compose.prod.yml /tmp/gn/bin /opt/gymnotebook/
   sudo chown -R root:root /opt/gymnotebook
   ```

3. **User, group and secrets.** `adduser` creates the `gymdeploy` group with
   the user. The password is disabled, so the only way in is the key from
   § Deploy. `admin` joins the group to read the env file; the tracker's
   `deploy` user must not.

   ```sh
   sudo adduser --disabled-password --gecos "" gymdeploy
   sudo usermod -aG docker gymdeploy
   sudo usermod -aG gymdeploy admin      # takes effect at the next login
   sudo install -d -o root -g gymdeploy -m 0750 /etc/gymnotebook
   sudo install -o root -g gymdeploy -m 0640 /dev/null /etc/gymnotebook/.env
   sudo install -o root -g gymdeploy -m 0640 /etc/subscription-tracker/upcloud-ca.pem \
     /etc/gymnotebook/upcloud-ca.pem
   ```

4. **Fill the env file** per § Environment with `sudo -e /etc/gymnotebook/.env`.
   The three **from Azure** values are read once, from your machine, while
   the Container App still exists:

   ```sh
   az containerapp secret list -g rg-gymnotebook-prod -n <api app> --show-values \
     --query "[?name=='jwt-secret' || name=='resend-api-key' || name=='turnstile-secret-key'].{name:name,value:value}" -o table
   ```

   Paste each value straight into the editor; don't save them anywhere else.

5. **State directory and first tag.** The tag is the current `main` build,
   `main-<40 hex sha>` (GHCR, or the last `deploy` run on GitHub):

   ```sh
   sudo install -d -o gymdeploy -g gymdeploy -m 0755 /var/lib/gymnotebook
   echo IMAGE_TAG=main-<40 hex> | sudo -u gymdeploy tee /var/lib/gymnotebook/image-tag.env
   ```

6. **Site file.** Copy it where the tracker's Caddy reads it, then reload
   from the tracker's directory. `caddy reload` validates the whole config
   first and keeps the old one if anything is wrong, so the tracker keeps
   serving either way:

   ```sh
   sudo install -o root -g root -m 0644 /tmp/gn/gymnotebook.caddy /opt/edge/sites/gymnotebook.caddy
   rm -rf /tmp/gn
   docker compose exec -w /etc/caddy caddy caddy reload
   ```

   (`docker compose` without `-p` here on purpose: that is the tracker's
   project, where `caddy` runs.)

7. **DNS.** At Cloudflare, a **grey-cloud** (DNS only) A record for
   `api.gymnotebook.fit` pointing at the server's public IPv4. Leave
   `gymnotebook.fit` on Azure until § Cutover.

8. **First start:**

   ```sh
   gn up -d --wait
   curl https://api.gymnotebook.fit/health
   ```

   `gym-api`'s entrypoint applies the migrations to the empty database first.
   Pass: `{"status":"ok","database":"reachable"}` and a valid certificate.
   If the certificate fails, `docker compose logs caddy | grep -i gymnotebook`
   (tracker's project).

### Isolation checks

Run once after provisioning (quickstart § Provision):

- As the tracker's role: `psql … -d gymnotebook` → `permission denied for
  database "gymnotebook"`. As `gymnotebook`: `psql … -d defaultdb` → the same.
- `stat -c '%U:%G %a' /etc/gymnotebook/.env` → `root:gymdeploy 640`.
- `docker ps --filter name=gymnotebook` shows no published ports.

## Removing Gym Notebook

Takes Gym Notebook off the server without touching the tracker:

```sh
gn down
sudo rm /opt/edge/sites/gymnotebook.caddy
docker compose exec -w /etc/caddy caddy caddy reload    # tracker's project
```

Then, as `upadmin` (step 1's `psql` command):

```sql
DROP DATABASE gymnotebook;
DROP ROLE gymnotebook;
```

Optionally remove `/opt/gymnotebook`, `/etc/gymnotebook`, `/var/lib/gymnotebook`,
the `gymdeploy` user and the `api.gymnotebook.fit` DNS record. The `edge`
network and `/opt/edge/sites` belong to the tracker; leave them.

## Deploy

Merges to `main` deploy themselves: after `build-and-push` succeeds,
`deploy.yml` connects as `gymdeploy` and sends only the image tag. The key
can do nothing else: its `authorized_keys` line forces `deploy.sh` whatever
command the client asks for (contract:
[deploy-interface.md](../specs/005-upcloud-migration/contracts/deploy-interface.md)).

**Setup**, once, after § Provision:

1. Make the key on your machine, with no passphrase (CI can't type one):

   ```sh
   ssh-keygen -t ed25519 -C gha-gymnotebook-deploy -f gha-gymnotebook-deploy -N ''
   ```

2. On the server, install the public key with the forced command:

   ```sh
   sudo install -d -o gymdeploy -g gymdeploy -m 0700 /home/gymdeploy/.ssh
   echo 'restrict,command="/opt/gymnotebook/bin/deploy.sh" ssh-ed25519 <base64 key> gha-gymnotebook-deploy' \
     | sudo -u gymdeploy tee /home/gymdeploy/.ssh/authorized_keys
   sudo chmod 0600 /home/gymdeploy/.ssh/authorized_keys
   ```

   `restrict` turns off port forwarding, agent forwarding and the terminal.
   `gymdeploy` is in the `docker` group, which is root-equivalent, so the
   forced command (not Unix permissions) is what separates this key from
   the tracker. That is the same trust the tracker's own deploy key has.

3. Check it refuses anything else:
   `ssh -i gha-gymnotebook-deploy gymdeploy@<server> 'id; ls'` → `deploy:
   refused; …`, exit status 64, no shell.

4. In GitHub, environment `production`: secrets `UPCLOUD_DEPLOY_SSH_KEY`
   (the private key file), `UPCLOUD_DEPLOY_HOST` (the server's IPv4) and
   `UPCLOUD_KNOWN_HOSTS` (`ssh-keyscan -t ed25519 <server>`, checked against
   `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the server). Then
   the repository variable `UPCLOUD_DEPLOY_ENABLED=true`. Delete the private
   key from your machine.

**What a deploy does**: refuse anything but `main-<40 hex>`; take a lock;
remember the running tag; pull the new images; `up -d --wait`; check
`https://api.gymnotebook.fit/health` through Caddy on this machine; on
failure, put the previous tag back. It only touches project `gymnotebook`.

| Exit | Meaning | What to do |
|---|---|---|
| 0 | new tag live and healthy | nothing |
| 1 | new tag failed; previous tag restored | read the job log and `gn logs gym-api`; fix forward |
| 2 | rollback failed, or no previous tag | production is down: roll back by hand, below |
| 64 | invalid tag; nothing ran | check the tag in the workflow |
| 75 | image pull failed; nothing changed | GHCR or network; re-run the job |

**Rolling back by hand**: run the `deploy` workflow with `workflow_dispatch`
and an older `main-<40 hex>` tag from GHCR. If CI itself is the problem, on
the server:

```sh
echo IMAGE_TAG=main-<40 hex> | sudo -u gymdeploy tee /var/lib/gymnotebook/image-tag.env
gn up -d --wait
```

## Cutover

The one-time move from Azure + Neon (quickstart § Cutover, FR-011).
Preconditions: G2 passed (tasks T045) and the privacy amendment is signed and
merged (T047). There is no maintenance page: the owner is the only active
user and simply stops logging for the duration.

`NEON_URL` is Neon's **unpooled** connection string with `sslmode=require`
(Neon advises against dumping through the pooler). `GN_URL` is the
`gymnotebook` role in libpq form:
`postgresql://gymnotebook:<pw>@<host>:<port>/gymnotebook?sslmode=verify-full&sslrootcert=/etc/gymnotebook/upcloud-ca.pem`.
Set both with `read -rs` so they stay out of the shell history.

**A day before**: at Cloudflare, lower the `gymnotebook.fit` record's TTL to
60 s.

**1. Freeze.** Stop logging. In Neon's SQL editor, so Neon stays an exact
rollback copy:

```sql
ALTER DATABASE <db> SET default_transaction_read_only = on;
SELECT pg_terminate_backend(pid) FROM pg_stat_activity
WHERE datname = '<db>' AND pid <> pg_backend_pid();
```

**2. Copy**, on the server. `gym-api` is stopped so nothing writes to the
target, which must be empty apart from the migrations (§ Emptying the
database after rehearsal, if the rehearsal used it):

```sh
read -rs NEON_URL; read -rs GN_URL; export NEON_URL GN_URL
gn stop gym-api
cd /tmp
docker run --rm -e NEON_URL -v "$PWD:/work" postgres:18 \
  sh -c 'pg_dump -Fc --no-owner --no-privileges -f /work/neon.dump "$NEON_URL"'
docker run --rm -e GN_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
  sh -c 'psql "$GN_URL" -v ON_ERROR_STOP=1 -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;" \
    && pg_restore --no-owner --no-privileges --exit-on-error -d "$GN_URL" /work/neon.dump \
    && psql "$GN_URL" -c ANALYZE'
```

Dropping and recreating `public` as `gymnotebook` clears the tables the
first start's migrations created, so the restore lands in an empty schema
owned by our role.

**3. Verify.** Both outputs must be identical (SC-002); `diff` prints nothing:

```sh
docker run --rm -e NEON_URL -v /opt/gymnotebook/bin:/bin-gn:ro postgres:18 \
  sh -c 'psql -XAt -v ON_ERROR_STOP=1 -f /bin-gn/verify-copy.sql "$NEON_URL"' > neon.txt
docker run --rm -e GN_URL -v /opt/gymnotebook/bin:/bin-gn:ro \
  -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
  sh -c 'psql -XAt -v ON_ERROR_STOP=1 -f /bin-gn/verify-copy.sql "$GN_URL"' > upcloud.txt
diff neon.txt upcloud.txt && cat upcloud.txt
```

Keep `upcloud.txt`: the final Neon archive is checked against it. If `diff`
prints anything, stop: § Rollback.

**4. Start and switch.** `gn up -d --wait`. At Cloudflare, point the
`gymnotebook.fit` A record (grey cloud) at the server. Watch Caddy obtain the
certificate: `docker compose logs -f caddy | grep -i gymnotebook.fit`
(tracker's project).

**5. Smoke test** (T049):

- No certificate warning on `gymnotebook.fit` and `api.gymnotebook.fit`.
- Sign in with the existing password (or an existing session still works).
- Notebook, chart and last-backup date unchanged; log one set; take a JSON
  backup.
- With a second address: signup → confirm email → sign in → password reset.
  The Turnstile widget appears.

Any failure: § Rollback. Otherwise delete `/tmp/neon.dump` once the final
archive is made (spec task T050).

## Rollback

Valid only while Azure and Neon still exist, that is, until spec task T056
deletes them.

1. At Cloudflare, point `gymnotebook.fit` back to `20.240.228.206`.
2. In Neon: `ALTER DATABASE <db> SET default_transaction_read_only = off;`
3. Anything written on UpCloud after step 4 of § Cutover is re-entered by
   hand on Azure. The window is minutes and the owner is the only writer.

The UpCloud stack can keep running on `api.gymnotebook.fit` while the cause
is found.

## Restore

Recovers the `gymnotebook` database from UpCloud's daily backups (kept
3 days; FR-020). UpCloud restores a whole service, never one database, and
the service is shared with the tracker, so the backup is restored into a
**new, temporary** service and only our database is copied back. The
tracker's database is never touched. The temporary service bills hourly
while it exists: cents. Users' long-term copy remains the in-app JSON backup
(spec 004).

1. In the UpCloud console, open the database → Backups → restore to a new
   service: `fi-hel1`, the **same SDN network**, public access off. Note its
   private host and port; its admin is `upadmin` with that service's
   password.
2. Dump our database from it. `TMP_URL` has `GN_URL`'s form with that
   service's host and port, user `upadmin` and its password, database
   `gymnotebook`:

   ```sh
   read -rs TMP_URL; read -rs GN_URL; export TMP_URL GN_URL
   cd /tmp
   docker run --rm -e TMP_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
     sh -c 'pg_dump -Fc --no-owner --no-privileges -f /work/restore.dump "$TMP_URL"'
   ```

   If the temporary service's certificate is signed by a different CA,
   download that service's CA and mount it instead.
3. Replace the live data, as `gymnotebook`, with `gym-api` stopped:

   ```sh
   gn stop gym-api
   docker run --rm -e GN_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
     sh -c 'pg_restore --clean --if-exists --no-owner --no-privileges -d "$GN_URL" /work/restore.dump \
       && psql "$GN_URL" -c ANALYZE'
   gn up -d --wait
   ```

4. Check the site, run `verify-copy.sql` (§ Cutover step 3, with `GN_URL`)
   against the counts you expect, then delete the temporary service and
   `/tmp/restore.dump`.

## Emptying the database after rehearsal

Before cutover the rehearsal data (test account, the 100,000-set restore)
must go, so the cutover copy lands in an empty database (quickstart
rehearsal step 7). As `upadmin` (§ Provision step 1's `psql` command):

```sh
gn stop gym-api
```

```sql
DROP DATABASE gymnotebook;
CREATE DATABASE gymnotebook OWNER gymnotebook;
REVOKE CONNECT ON DATABASE gymnotebook FROM PUBLIC;
GRANT CONNECT ON DATABASE gymnotebook TO gymnotebook;
```

```sh
gn up -d --wait     # migrations recreate the schema
curl https://api.gymnotebook.fit/health
```

`DROP DATABASE` refuses while connections are open; stopping `gym-api`
first is what closes them.
