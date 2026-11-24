#!/bin/sh
# Старт контроллера: zerotier-one (встроенный контроллер) + прокси к его API.
set -e
ZT_HOME=/var/lib/zerotier-one
mkdir -p "$ZT_HOME"

# Восстановление identity из бандла флота: volume пропал, а ID сети (= адрес контроллера)
# должен остаться прежним, иначе клиенты со старым указателем в сеть не войдут.
if [ ! -f "$ZT_HOME/identity.secret" ] && [ -n "$ZT_IDENTITY_SECRET" ]; then
  echo "identity: восстанавливаю из ZT_IDENTITY_SECRET"
  printf '%s\n' "$ZT_IDENTITY_SECRET" > "$ZT_HOME/identity.secret"
  zerotier-idtool getpublic "$ZT_HOME/identity.secret" > "$ZT_HOME/identity.public"
  chmod 600 "$ZT_HOME/identity.secret"
fi

zerotier-one -d
for i in $(seq 1 60); do
  [ -f "$ZT_HOME/authtoken.secret" ] && zerotier-cli info >/dev/null 2>&1 && break
  sleep 1
done
echo "zerotier: $(zerotier-cli info 2>&1)"

exec /app/SzDiag.ZtController
