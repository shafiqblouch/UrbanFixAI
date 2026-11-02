#!/usr/bin/env bash
# Creates an Ubuntu VM in Azure and starts the UrbanFix test stack on it (see docs/deploy-azure.md).
# Run in Azure Cloud Shell (Bash). Required: export OPENROUTER_API_KEY=<a new key>.
# Optional: RG, LOC, SIZE, BRANCH. If the size or region is not available on your subscription, pick another (az vm list-skus).
set -euo pipefail

: "${OPENROUTER_API_KEY:?Run: export OPENROUTER_API_KEY=<your key> first}"
RG="${RG:-urbanfix-rg}"
LOC="${LOC:-westeurope}"
SIZE="${SIZE:-Standard_B2s}"
BRANCH="${BRANCH:-wave-3-to-5-complete}"
LABEL="${LABEL:-urbanfix-$RANDOM}"
HOST="$LABEL.$LOC.cloudapp.azure.com"

CLOUD_INIT="$(mktemp)"
cat > "$CLOUD_INIT" <<EOF
#cloud-config
write_files:
  - path: /opt/setup-urbanfix.sh
    permissions: '0700'
    content: |
      #!/usr/bin/env bash
      set -euo pipefail
      curl -fsSL https://get.docker.com | sh
      git clone --depth 1 --branch $BRANCH https://github.com/shafiqblouch/UrbanFixAI.git /opt/UrbanFixAI
      cd /opt/UrbanFixAI/deploy/azure
      rnd() { openssl rand -hex 16; }
      cat > .env <<ENV
      PUBLIC_HOST=$HOST
      DB_PASSWORD=\$(rnd)
      APP_ROLE_PASSWORD=\$(rnd)
      ADMIN_PASSWORD=\$(rnd)
      AI_SERVICE_TOKEN=\$(rnd)
      OPENROUTER_API_KEY=$OPENROUTER_API_KEY
      STORAGE_ACCESS_KEY=urbanfixmedia
      STORAGE_SECRET_KEY=\$(rnd)
      ENV
      chmod 600 .env
      docker compose up -d --build
runcmd:
  - [ bash, -c, "/opt/setup-urbanfix.sh > /var/log/urbanfix-setup.log 2>&1" ]
EOF

echo "Creating resource group $RG in $LOC..."
az group create -n "$RG" -l "$LOC" -o none

echo "Creating static public IP with DNS name $HOST..."
az network public-ip create -g "$RG" -n urbanfix-ip --sku Standard --allocation-method Static --dns-name "$LABEL" -o none

echo "Creating the VM ($SIZE)... this takes a minute or two."
az vm create -g "$RG" -n urbanfix-vm --image Ubuntu2204 --size "$SIZE" \
  --admin-username azureuser --generate-ssh-keys \
  --public-ip-address urbanfix-ip --custom-data "$CLOUD_INIT" -o none

echo "Opening ports 80, 443 and 9000..."
az vm open-port -g "$RG" -n urbanfix-vm --port 80 --priority 1010 -o none
az vm open-port -g "$RG" -n urbanfix-vm --port 443 --priority 1020 -o none
az vm open-port -g "$RG" -n urbanfix-vm --port 9000 --priority 1030 -o none
rm -f "$CLOUD_INIT"

cat <<DONE

VM created. Setup now runs on the VM; the first build takes 10 to 20 minutes.
  Public address : https://$HOST
  Health check   : https://$HOST/health/ready   (returns {"status":"ready"} when done)
  Setup log      : ssh azureuser@$HOST 'sudo tail -f /var/log/urbanfix-setup.log'
  Passwords      : ssh azureuser@$HOST 'sudo cat /opt/UrbanFixAI/deploy/azure/.env'
  Stop billing   : az vm deallocate -g $RG -n urbanfix-vm
  Delete all     : az group delete -n $RG
DONE
