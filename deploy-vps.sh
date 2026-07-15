#!/usr/bin/env bash
#
# Bootstrap the VPS for the SSO demo. Run ON the box, once, as root or a sudo user:
#
#   curl -fsSL https://raw.githubusercontent.com/bulubuloa/Aspire.SSO.POC/main/deploy-vps.sh | bash
#   # or: scp it over and  ./deploy-vps.sh setup
#
# Afterwards CI (.github/workflows/deploy.yml) handles every deploy on push to main.
#
set -euo pipefail

DOMAIN=aspireservice.online
APP_DIR=/opt/sso
GH_OWNER=bulubuloa

BOLD=$'\033[1m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RED=$'\033[31m'; OFF=$'\033[0m'
say(){ printf "${BOLD}==>${OFF} %s\n" "$1"; }
ok(){  printf "  ${GREEN}✓${OFF} %s\n" "$1"; }
warn(){ printf "  ${YELLOW}!${OFF} %s\n" "$1"; }

need_root() { [ "$(id -u)" -eq 0 ] || { echo "${RED}Run as root (or: sudo $0 $*)${OFF}"; exit 1; }; }

cmd_setup() {
  need_root "$@"

  say "Swap (1GB RAM is tight — this turns a potential OOM-kill into a slow moment)"
  if [ ! -f /swapfile ]; then
    fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap -q /swapfile && swapon /swapfile
    grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
    sysctl -qw vm.swappiness=10
    grep -q 'vm.swappiness' /etc/sysctl.conf || echo 'vm.swappiness=10' >> /etc/sysctl.conf
    ok "2GB swap on"
  else ok "swap already present"; fi

  say "Docker"
  if ! command -v docker >/dev/null; then
    curl -fsSL https://get.docker.com | sh >/dev/null
    systemctl enable --now docker
    ok "docker installed"
  else ok "docker already installed"; fi

  say "Firewall"
  if command -v ufw >/dev/null; then
    ufw allow 22/tcp >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null
    ufw --force enable >/dev/null
    ok "ufw: 22, 80, 443 only"
  else warn "ufw not present — make sure only 22/80/443 are exposed"; fi

  say "App directory"
  mkdir -p "$APP_DIR"/demo
  cd "$APP_DIR"

  # .env holds the one real secret. CI never touches it, so a redeploy can't clobber it.
  if [ ! -f .env ]; then
    SECRET=$(openssl rand -hex 24)
    read -rp "  Email for Let's Encrypt: " ACME_EMAIL
    cat > .env <<EOF
GH_OWNER=$GH_OWNER
ACME_EMAIL=$ACME_EMAIL
CLIENT_SECRET=$SECRET
TAG=latest
EOF
    chmod 600 .env
    ok ".env written (secret generated, 0600)"
  else ok ".env already exists — keeping it"; fi

  say "DNS check"
  IP=$(curl -fsS4 ifconfig.me || echo "?")
  echo "     this box: $IP"
  for h in client aspire demo; do
    R=$(getent hosts "$h.$DOMAIN" | awk '{print $1}' | head -1)
    if [ "$R" = "$IP" ]; then ok "$h.$DOMAIN → $IP"
    elif [ -z "$R" ];  then warn "$h.$DOMAIN does not resolve yet — add an A record → $IP"
    else warn "$h.$DOMAIN → $R  (expected $IP)"; fi
  done

  echo
  say "Next"
  cat <<EOF
  1. Point these A records at ${BOLD}$IP${OFF}:
       client.$DOMAIN     aspire.$DOMAIN     demo.$DOMAIN
  2. Add these GitHub secrets (repo → Settings → Secrets → Actions):
       VPS_HOST     $IP
       VPS_USER     $(logname 2>/dev/null || echo root)
       VPS_SSH_KEY  <a private key whose public half is in ~/.ssh/authorized_keys here>
  3. Push to main — CI builds, pushes images, and deploys here automatically.
     Or bring it up right now:  $0 update

  ${YELLOW}Caddy gets certs on first request — DNS must resolve FIRST or Let's Encrypt will fail
  and rate-limit you for an hour.${OFF}
EOF
}

cmd_update() {
  cd "$APP_DIR"
  [ -f .env ] || { echo "${RED}No $APP_DIR/.env — run '$0 setup' first${OFF}"; exit 1; }
  say "Pulling images"
  docker compose pull client aspire
  say "Restarting"
  docker compose up -d
  docker image prune -f >/dev/null
  cmd_status
}

cmd_status() {
  cd "$APP_DIR"
  say "Containers"; docker compose ps
  echo; say "Memory"; free -h | sed 's/^/  /'
  echo; say "Endpoints"
  for u in "https://client.$DOMAIN/api/rewards" "https://aspire.$DOMAIN/api/session" "https://demo.$DOMAIN"; do
    printf "  %-52s %s\n" "$u" "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$u")"
  done
}

cmd_logs() { cd "$APP_DIR"; docker compose logs -f --tail=100 "${1:-}"; }

case "${1:-setup}" in
  setup)  cmd_setup "$@" ;;
  update) cmd_update ;;
  status) cmd_status ;;
  logs)   cmd_logs "${2:-}" ;;
  *) echo "Usage: $0 [setup|update|status|logs [service]]"; exit 1 ;;
esac
