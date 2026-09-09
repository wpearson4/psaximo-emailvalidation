#!/bin/sh
set -eu

secret_file=/var/lib/postsrsd/postsrsd.secret

if [ ! -s "$secret_file" ]; then
    umask 077
    temporary_secret="${secret_file}.tmp.$$"
    head -c 32 /dev/urandom | base64 >"$temporary_secret"
    chown root:root "$temporary_secret"
    mv "$temporary_secret" "$secret_file"
fi

chmod 0600 "$secret_file"
postfix check

postsrsd -C /etc/postsrsd/postsrsd.conf &
postsrsd_pid=$!
postfix start-fg &
postfix_pid=$!

terminate() {
    postfix stop >/dev/null 2>&1 || true
    kill -TERM "$postsrsd_pid" "$postfix_pid" >/dev/null 2>&1 || true
    wait "$postsrsd_pid" >/dev/null 2>&1 || true
    wait "$postfix_pid" >/dev/null 2>&1 || true
}

trap terminate TERM INT

while kill -0 "$postsrsd_pid" 2>/dev/null && kill -0 "$postfix_pid" 2>/dev/null; do
    sleep 5 &
    wait $! || true
done

terminate
exit 1
