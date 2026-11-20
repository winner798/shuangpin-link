#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 6 || $EUID -ne 0 ]]; then
    echo 'Usage (as root): deploy-ubuntu.sh SOURCE_DIR CADDYFILE SITE CADDY_BIN CADDY_SERVICE signal|systemd|restart' >&2
    echo 'Pass the SHA-256 digest of the setup code on stdin for a fresh deployment.' >&2
    exit 2
fi

source_dir=$1
config=$2
site=$3
caddy=$4
caddy_service=$5
reload_method=$6
setup_file=/etc/shuangpin-link/setup.sha256
relay_service=shuangpin-link.service
scratch=$(mktemp -d)
updated_caddy=0
first_service=0
old_service_running=0

reload_caddy() {
    case "$reload_method" in
        signal) systemctl kill --kill-who=main --signal=USR1 "$caddy_service" ;;
        systemd) systemctl reload "$caddy_service" ;;
        restart) systemctl restart "$caddy_service" ;;
        *) return 1 ;;
    esac
}

replace_config() {
    local from=$1
    local temp
    temp=$(mktemp "$(dirname "$config")/.shuangpin-link.XXXXXX")
    cp --attributes-only --preserve=all -- "$config" "$temp"
    cat "$from" > "$temp"
    mv -T -- "$temp" "$config"
}

finish() {
    local code=$?
    trap - EXIT
    if (( code != 0 )); then
        echo 'Deployment failed. Restoring previous configuration.' >&2
        if (( updated_caddy )); then
            replace_config "$scratch/original" && reload_caddy || true
        fi
        if (( first_service )); then
            systemctl disable --now "$relay_service" >/dev/null 2>&1 || true
            rm -f /etc/systemd/system/shuangpin-link.service
        elif [[ -f $scratch/old-service ]]; then
            install -m 0644 "$scratch/old-service" /etc/systemd/system/shuangpin-link.service
            if [[ -f $scratch/old-relay ]]; then
                install -m 0644 "$scratch/old-relay" /opt/shuangpin-link/relay.py
            fi
            if (( old_service_running )); then
                systemctl daemon-reload && systemctl restart "$relay_service" || true
            fi
        fi
        systemctl daemon-reload || true
    fi
    rm -rf -- "$scratch"
    exit "$code"
}
trap finish EXIT

test -f "$config"
test -x "$caddy"
systemctl is-active --quiet "$caddy_service"
if [[ $reload_method == signal ]]; then
    python3 - "$caddy" "$caddy_service" "$config" <<'PY'
import os, re, subprocess, sys
caddy, unit, config = sys.argv[1:]
version = re.search(r'\bv(\d+)\.(\d+)\.(\d+)', subprocess.check_output([caddy, 'version'], text=True))
if not version or tuple(map(int, version.groups())) < (2, 11, 0):
    sys.exit('This Caddy version does not support signal reload.')
pid = subprocess.check_output(['systemctl', 'show', '-p', 'MainPID', '--value', unit], text=True).strip()
with open(f'/proc/{pid}/cmdline', 'rb') as process:
    command = [os.fsdecode(arg) for arg in process.read().split(b'\0')]
if 'run' not in command or '--resume' in command or config not in command:
    sys.exit('The current Caddy process cannot reload this file by signal.')
PY
elif [[ $reload_method != restart && $reload_method != systemd ]]; then
    exit 2
fi

curl -fsS --max-time 7 -o /dev/null "https://$site/"
cp --preserve=all -- "$config" "$scratch/original"
python3 - "$config" "$scratch/Caddyfile" "$site" <<'PY'
from pathlib import Path
import re, sys
src, dst, site = sys.argv[1:]
original = Path(src).read_text()
marker = '# BEGIN shuangpin-link'
block = '''
    # BEGIN shuangpin-link
    handle_path /ime-sync/* {
        request_body {
            max_size 12MB
        }
        reverse_proxy 127.0.0.1:18765
    }
    # END shuangpin-link
'''
if marker not in original:
    if '/ime-sync/' in original:
        sys.exit('An unmanaged /ime-sync route already exists.')
    pattern = rf'(?m)^{re.escape(site)}\s*\{{[ \t]*\n'
    rendered, n = re.subn(pattern, lambda match: match[0] + block, original)
    if n != 1:
        sys.exit('Expected one exact existing HTTPS site block.')
else:
    rendered, n = re.subn(r'(?ms)^ *# BEGIN shuangpin-link\n.*?^ *# END shuangpin-link\n',
                          lambda _: block.lstrip('\n'), original)
    if n != 1:
        sys.exit('Unexpected managed route markers.')
Path(dst).write_text(rendered)
PY
"$caddy" validate --config "$scratch/Caddyfile" --adapter caddyfile

if [[ ! -f $setup_file ]]; then
    IFS= read -r setup_hash
    [[ $setup_hash =~ ^[a-f0-9]{64}$ ]]
    install -d -m 0700 /etc/shuangpin-link
    printf '%s\n' "$setup_hash" > "$scratch/setup.sha256"
    install -m 0600 "$scratch/setup.sha256" "$setup_file"
    unset setup_hash
fi
if [[ -f /etc/systemd/system/$relay_service ]]; then
    cp -a /etc/systemd/system/"$relay_service" "$scratch/old-service"
    cp -a /opt/shuangpin-link/relay.py "$scratch/old-relay"
    if systemctl is-active --quiet "$relay_service"; then old_service_running=1; fi
else
    first_service=1
fi
install -d -m 0755 /opt/shuangpin-link
install -m 0644 "$source_dir/relay.py" /opt/shuangpin-link/relay.py
install -m 0644 "$source_dir/shuangpin-link.service" /etc/systemd/system/"$relay_service"
systemctl daemon-reload
systemctl enable "$relay_service" >/dev/null
systemctl restart "$relay_service"
for attempt in {1..12}; do
    if curl -fsS --max-time 3 http://127.0.0.1:18765/health | grep -q '"protocol":1'; then break; fi
    sleep 1
done
curl -fsS --max-time 3 http://127.0.0.1:18765/health | grep -q '"protocol":1'

if ! cmp -s "$scratch/Caddyfile" "$config"; then
    updated_caddy=1
    replace_config "$scratch/Caddyfile"
    reload_caddy
fi
for attempt in {1..12}; do
    if curl -fsS --max-time 3 "https://$site/ime-sync/health" | grep -q '"protocol":1'; then break; fi
    sleep 1
done
curl -fsS --max-time 5 "https://$site/ime-sync/health" | grep -q '"protocol":1'
curl -fsS --max-time 5 -o /dev/null "https://$site/"
systemctl show "$relay_service" -p ActiveState -p MemoryCurrent
echo 'Existing HTTPS site and sync endpoint verified.'
