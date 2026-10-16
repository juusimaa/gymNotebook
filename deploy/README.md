# deploy/: production on UpCloud

Gym Notebook runs on the Subscription Tracker's UpCloud server (`fi-hel1`),
beside the tracker and isolated from it: its own containers with hard memory
and CPU caps, its own database and role in the shared managed PostgreSQL 18
service, its own secrets file and its own deploy key. TLS and routing are the
tracker's Caddy, which imports our site file. The design is
[specs/005-upcloud-migration/](../specs/005-upcloud-migration/); this file is
how to run it.

Since the cutover on 2026-10-12 this server is production for both
`gymnotebook.fit` and `api.gymnotebook.fit` (§ Cutover).

| File | On the server | What it is |
|---|---|---|
| `compose.prod.yml` | `/opt/gymnotebook/compose.prod.yml` | the two app containers, `gym-api` and `gym-web` |
| `gymnotebook.caddy` | `/opt/edge/sites/gymnotebook.caddy` | our site blocks, imported by the tracker's Caddyfile |
| `bin/deploy.sh` | `/opt/gymnotebook/bin/deploy.sh` | the deploy key's forced command |
| `bin/verify-copy.sql` | `/opt/gymnotebook/bin/verify-copy.sql` | row counts per table, to compare two databases |
| `bin/trim-logs.sh` | `/opt/gymnotebook/bin/trim-logs.sh` | removes container log lines older than 30 days (§ Log time limit) |
| `systemd/gymnotebook-log-trim.*` | `/etc/systemd/system/` | runs `trim-logs.sh` daily as root |
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
| `/var/lib/gymnotebook/deletion-evidence.log` | `gymdeploy 0600` | `deploy.sh`; trimmed by `trim-logs.sh` | replaced `gym-api` containers' deletion lines and per-minute health marks (§ Deletion evidence) |

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

**Don't recreate `gym-api` by hand.** Docker deletes a container's log when
the container is recreated, and that log holds the deletion lines a restore
may need (§ Deletion evidence). `gn stop`, `gn start` and `gn restart` keep
it. `gn up` recreates only when the image or configuration changed, and
`gn down` always does. To change the version, run `deploy.sh` (§ Deploy,
rolling back by hand), which saves the lines first.

## Environment

`/etc/gymnotebook/.env` is the only place app secrets exist in production
(contract: [server-env.md](../specs/005-upcloud-migration/contracts/server-env.md)).
GitHub holds only the deploy key, host and known-hosts line. Edit with
`sudo -e /etc/gymnotebook/.env`, apply by redeploying the running tag
(§ Deploy), which saves the deletion evidence before it recreates `gym-api`;
not with `gn up`, which would delete it. Never copy a
value from a laptop `.env`, and never paste one into a PR, issue or chat.

| Variable | Where the value comes from |
|---|---|
| `ConnectionStrings__Default` | the `gymnotebook` role from § Provision: `Host=<private host>;Port=<port>;Database=gymnotebook;Username=gymnotebook;Password=<pw>;SSL Mode=VerifyFull;Root Certificate=/etc/gymnotebook/upcloud-ca.pem;Maximum Pool Size=10` |
| `Jwt__Secret` | **carried over**: copy it from the current env file so signed-in sessions survive a move; a new one (`openssl rand -base64 48`) signs everyone out |
| `Jwt__ExpiryMinutes` | `30` |
| `CORS_ORIGINS` | `https://gymnotebook.fit` |
| `PRIVACY_LIFECYCLE_ENABLED` | `false` |
| `DEMO_MODE_ENABLED` | `false` (absent is the same: fails closed); read by `gym-api` and `gym-web` |
| `Email__Backend` | `resend` |
| `EMAIL_FROM` | `Gym Notebook <no-reply@mail.gymnotebook.fit>` |
| `APP_URL` | `https://gymnotebook.fit` |
| `RESEND_API_KEY` | **carried over** from the current env file, or a new sending key from Resend |
| `TURNSTILE_SECRET_KEY` | **carried over** from the current env file, or from the Turnstile widget at Cloudflare |
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

Check the role part before step 1; it is easy to apply the edge and miss it
(2026-10-12 provisioning did). As `upadmin`, from any database:

```sql
SELECT datname, datdba::regrole AS owner, datacl
FROM pg_database WHERE datname = 'defaultdb';
```

Pass: owner `tracker`, and no `c` after a bare `=` in `datacl` (`=Tc/…`
means every role may connect). Otherwise apply the tracker's role steps
first.

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

   Log out and back in before step 8: `gn` reads the env file as `admin`,
   and without the group it fails with `permission denied`.
   `id -nG | grep -w gymdeploy` confirms it.

4. **Fill the env file** per § Environment with `sudo -e /etc/gymnotebook/.env`.
   On a new server, the three **carried over** values come from the old
   server's env file (`sudo cat /etc/gymnotebook/.env` there). Paste each
   value straight into the editor; don't save them anywhere else.

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
   docker compose -f /opt/subscription-tracker/compose.prod.yml exec -w /etc/caddy caddy caddy reload
   ```

   `/tmp/gn` from step 2 may be gone if the server rebooted in between;
   copy the site file again. The `-f` names the tracker's stack, where
   `caddy` runs, instead of relying on the login shell's `COMPOSE_FILE`.
   A "Caddyfile input is not formatted" warning is about the tracker's
   file and harmless.

7. **DNS.** At Cloudflare, **grey-cloud** (DNS only) A records for
   `api.gymnotebook.fit` and `gymnotebook.fit` pointing at the server's
   public IPv4. While another server still serves production, point only
   `api` here and move `gymnotebook.fit` last, as § Cutover did.

   Caddy then gets the certificate on its own. Until `gym-api` runs,
   `curl https://api.gymnotebook.fit/health` gives **502 with a valid
   certificate**; that is the pass for this step. `tlsv1 alert internal
   error` means no certificate yet. Errors for `gymnotebook.fit` in Caddy's
   log are expected while that name still points elsewhere. If
   the `api` attempt hangs, typically on ZeroSSL after Let's Encrypt failed
   before the record existed, start a fresh one with `caddy reload --force`
   (command above), or `restart caddy` in the tracker's stack if a stale
   lock holds it (about a second of tracker downtime).

8. **First start:**

   ```sh
   gn up -d --wait
   curl https://api.gymnotebook.fit/health
   ```

   `gym-api`'s entrypoint applies the migrations to the empty database first.
   Pass: `{"status":"ok","database":"reachable"}` and a valid certificate.
   If the certificate fails, see step 7; Caddy's log is
   `docker compose -f /opt/subscription-tracker/compose.prod.yml logs caddy 2>&1 | grep 'api.gymnotebook'`.

### Isolation checks

Run once after provisioning (quickstart § Provision):

- As the tracker's role: `psql … -d gymnotebook` → `permission denied for
  database "gymnotebook"`. As `gymnotebook`: `psql … -d defaultdb` → the same.
- `stat -c '%U:%G %a' /etc/gymnotebook/.env` → `root:gymdeploy 640`.
- `docker ps --filter name=gymnotebook` shows no published ports.

### Log time limit

Docker caps each container's log at 3 × 10 MB but never by age, and the
privacy retention schedule allows 30 days at most
(`docs/privacy/retention.md`, signed decision T047). A daily timer runs
`bin/trim-logs.sh`, which removes older lines from `gym-api`'s and
`gym-web`'s logs and from the saved deletion evidence (§ Deletion
evidence). Install once per server, from a checkout of `main`:

```sh
rsync -r deploy/bin deploy/systemd admin@<server>:/tmp/gn/
ssh admin@<server>
sudo install -o root -g root -m 0755 /tmp/gn/bin/trim-logs.sh /opt/gymnotebook/bin/trim-logs.sh
sudo install -o root -g root -m 0644 /tmp/gn/systemd/gymnotebook-log-trim.service /tmp/gn/systemd/gymnotebook-log-trim.timer /etc/systemd/system/
rm -rf /tmp/gn
sudo systemctl daemon-reload
sudo systemctl enable --now gymnotebook-log-trim.timer
```

Check it:

- `systemctl list-timers gymnotebook-log-trim.timer` shows the next run.
- `sudo systemctl start gymnotebook-log-trim.service`, then
  `systemctl status gymnotebook-log-trim.service` → `status=0/SUCCESS`.
  `sudo journalctl -u gymnotebook-log-trim.service` lists only the files it
  trimmed, with line counts; on young containers it prints nothing.
- `gn logs --no-log-prefix --timestamps gym-api | head -1`, and the same for
  `gym-web`, shows no line older than 30 days.

The script must stay root-owned and not writable by `gymdeploy`: it runs as
root. The tracker's Caddy log is the tracker's and isn't trimmed here.

## Removing Gym Notebook

Takes Gym Notebook off the server without touching the tracker:

```sh
gn down
sudo systemctl disable --now gymnotebook-log-trim.timer
sudo rm /etc/systemd/system/gymnotebook-log-trim.service /etc/systemd/system/gymnotebook-log-trim.timer
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
   `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` on the server). Delete
   the private key from your machine.

**What a deploy does**: refuse anything but `main-<40 hex>`; take a lock;
remember the running tag; pull the new images; stop `gym-api` and save its
deletion evidence (§ Deletion evidence); `up -d --wait`; check
`https://api.gymnotebook.fit/health` through Caddy on this machine; on
failure, save the failed container's evidence too and put the previous tag
back. It only touches project `gymnotebook`.

| Exit | Meaning | What to do |
|---|---|---|
| 0 | new tag live and healthy | nothing |
| 1 | new tag failed; previous tag restored | read the job log and `gn logs gym-api`; fix forward |
| 2 | rollback failed, no previous tag, or the evidence save failed after `gym-api` stopped | production is down: read the job log; if it says the evidence save failed, fix that first (disk space, `/var/lib/gymnotebook` owner); then roll back by hand, below |
| 64 | invalid tag; nothing ran | check the tag in the workflow |
| 75 | image pull or evidence save failed; nothing changed | GHCR or network: re-run the job; evidence save: check disk space and `/var/lib/gymnotebook` first |

**Rolling back by hand**: run the `deploy` workflow with `workflow_dispatch`
and an older `main-<40 hex>` tag from GHCR. If CI itself is the problem, run
the same script on the server as `gymdeploy`, so the evidence is saved first.
Start from `/`: `sudo` keeps the current directory, and Compose fails with
`stat .: permission denied` in one `gymdeploy` can't read, such as
`/home/admin` (found in T063's install).

```sh
cd / && sudo -u gymdeploy env SSH_ORIGINAL_COMMAND=main-<40 hex> /opt/gymnotebook/bin/deploy.sh
```

**Redeploying the running tag** applies a changed env file the same way:

```sh
cd / && sudo -u gymdeploy sh -c 'SSH_ORIGINAL_COMMAND=$(sed -n "s/^IMAGE_TAG=//p" /var/lib/gymnotebook/image-tag.env) exec /opt/gymnotebook/bin/deploy.sh'
```

### Deletion evidence

Docker deletes a container's log when the container is recreated, and every
deploy recreates `gym-api`. The restore fallback
([docs/privacy/restore.md](../docs/privacy/restore.md) § 4) needs the
`deletion.*` lines from that log, and the health lines that show it has no
gaps. So before `up`, `deploy.sh` stops `gym-api` (no request can log after
the copy) and adds to `/var/lib/gymnotebook/deletion-evidence.log`:

- every `deletion.intent`, `deletion.committed` and `deletion.rolled_back`
  line, with Docker's timestamp;
- `<time> health` for the first health check of each minute;
- `<time> saved gym-api container <id>` for each save.

Nothing else from the log is kept. The file is sorted and de-duplicated,
`gymdeploy`-only (0600), and the daily log trim removes lines older than 30
days ([retention.md](../docs/privacy/retention.md)). Read it with
`sudo cat /var/lib/gymnotebook/deletion-evidence.log`; keep copies only in a
restore's private directory.

**Install** (once, then whenever `deploy.sh` or `trim-logs.sh` changes),
from a checkout of `main`:

```sh
rsync -r deploy/bin admin@<server>:/tmp/gn/
ssh admin@<server>
sudo install -o root -g root -m 0755 /tmp/gn/bin/deploy.sh /tmp/gn/bin/trim-logs.sh /opt/gymnotebook/bin/
rm -rf /tmp/gn
```

**Check it** after the next deploy:

- `sudo stat -c '%U %a' /var/lib/gymnotebook/deletion-evidence.log` →
  `gymdeploy 600`.
- `sudo tail -1 /var/lib/gymnotebook/deletion-evidence.log` → a
  `saved gym-api container` line from that deploy.
- `sudo grep -c ' health$' /var/lib/gymnotebook/deletion-evidence.log` is
  roughly the old container's lifetime in minutes.
- `sudo systemctl start gymnotebook-log-trim.service` → `status=0/SUCCESS`.

## Cutover

Done on 2026-10-12: Neon was frozen read-only, copied with `pg_dump` /
`pg_restore`, checked with `bin/verify-copy.sql` on both sides, and
`gymnotebook.fit` moved here. The record is in
[tasks T048–T050](../specs/005-upcloud-migration/tasks.md). Neon was
deleted the same day, so there is no rollback to the old hosting. The
runbook as it was run, with § Rollback, is in commit `9cbfdd8`
(`git show 9cbfdd8:deploy/README.md`): the starting point if Gym Notebook
ever moves again.

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
   `gymnotebook`. Each `read -rs` takes **one line**: paste each string on
   its own, unwrapped, or a line break splits it across variables (seen in
   the T058 drill). The keyword form
   `host=… port=… user=upadmin password=… dbname=gymnotebook sslmode=verify-full sslrootcert=/etc/gymnotebook/upcloud-ca.pem`
   works everywhere a URL does and needs no percent-encoding. The dump
   holds people's data, so it goes in a private directory, not `/tmp`:

   ```sh
   read -rs TMP_URL; export TMP_URL
   read -rs GN_URL; export GN_URL
   mkdir -m 700 ~/restore && cd ~/restore
   docker run --rm -e TMP_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
     sh -c 'pg_dump -Fc --no-owner --no-privileges -f /work/restore.dump "$TMP_URL"'
   ```

   The temporary service's certificate verified against our CA file in the
   2026-10-15 drill. If that ever fails, download that service's CA and
   mount it instead.
3. Replace the live data, as `gymnotebook`, with `gym-api` stopped:

   ```sh
   gn stop gym-api
   docker run --rm -e GN_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
     sh -c 'pg_restore --clean --if-exists --no-owner --no-privileges -d "$GN_URL" /work/restore.dump \
       && psql "$GN_URL" -c ANALYZE'
   ```

   Before starting `gym-api` again, remove every account deleted since the
   backup was taken, as [docs/privacy/restore.md](../docs/privacy/restore.md)
   says; otherwise the restore brings deleted accounts back. Then
   `gn start gym-api`, which keeps its log (§ Deletion evidence).

4. Check the counts against what you expect, then delete the temporary
   service and `rm -r ~/restore`:

   ```sh
   docker run --rm -e GN_URL -v /opt/gymnotebook/bin:/bin-gn:ro \
     -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
     sh -c 'psql -XAt -v ON_ERROR_STOP=1 -f /bin-gn/verify-copy.sql "$GN_URL"'
   ```

### Restore drill

Proves a backup restores without touching live data (FR-020; first run
2026-10-15, 15 minutes, T058). Do steps 1 and 2 above, with `~/drill` as the
private directory, then copy into a **scratch** database on the live service
instead of step 3. With `pg()` from
[docs/privacy/restore.md](../docs/privacy/restore.md) § 2, `ADMIN_URL` the
live service as `upadmin`, database `defaultdb`, and `DRILL_URL` the same
with database `gn_drill`:

```sh
pg TMP_URL -At -f /bin-gn/verify-copy.sql > backup.txt
pg ADMIN_URL -c 'CREATE DATABASE gn_drill'
docker run --rm -e DRILL_URL -v "$PWD:/work" -v /etc/gymnotebook/upcloud-ca.pem:/etc/gymnotebook/upcloud-ca.pem:ro postgres:18 \
  sh -c 'pg_restore --no-owner --no-privileges -d "$DRILL_URL" /work/restore.dump'
pg DRILL_URL -At -f /bin-gn/verify-copy.sql > drill.txt
diff backup.txt drill.txt && echo MATCH
```

Pass: only `MATCH`, within an hour of starting the restore. Then
`pg ADMIN_URL -c 'DROP DATABASE gn_drill'`, `cd ~ && rm -r ~/drill`,
`unset TMP_URL ADMIN_URL DRILL_URL`, and delete the temporary service in the
console (it also holds the tracker's database). If `psql` reports a local
socket (`/var/run/postgresql/...`), the variable it was given is empty or
has no host.

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
