# Azure DevOps release pipeline

`azure-pipelines.yml` defines the GitHub-backed pipeline in the OpenMeta Azure DevOps organization, `Search Api` project. It uses these existing resources:

- GitHub service connection: `wpearson4`
- Azure service connection: `OpenMeta Prod`
- Azure Container Registry: `acrpometadsiscussrch.azurecr.io`
- Production agent pool: `OMetaSearchPool`
- Production environment: `emailvalidation-production`

Every `master` update validates the .NET solution, Compose model, Nginx configuration, and Certbot image, then publishes both the immutable Git commit tag and `latest` to ACR. Production deployment is opt-in: manually run the pipeline with `deployProduction=true`. On the first deployment only, also set `bootstrapLetsEncrypt=true` and provide a monitored `letsencryptEmail` after confirming public ports 80 and 443 reach `10.10.252.31`.

## Self-hosted agent

Add one Linux agent to the existing `OMetaSearchPool` and run it on `esdata03` (`10.10.252.31`) as an unprivileged account such as `gwadmin`, never as root. In Azure DevOps, open **Organization settings → Agent pools → OMetaSearchPool → New agent**, select Linux x64, and use the displayed current download URL and checksum. Configure it with a short-lived PAT that has only **Agent Pools: Read & manage**:

```bash
mkdir -p /home/gwadmin/azagent
cd /home/gwadmin/azagent
# Download and verify the current Linux x64 agent package shown by Azure DevOps, then extract it here.
./config.sh --unattended \
  --url https://dev.azure.com/OpenMeta \
  --auth pat \
  --token 'PASTE-ONLY-IN-THIS-INTERACTIVE-SHELL' \
  --pool OMetaSearchPool \
  --agent esdata03-emailvalidation \
  --work _work \
  --replace
sudo ./svc.sh install gwadmin
sudo ./svc.sh start
sudo ./svc.sh status
```

Do not save the PAT in the repository, `.env`, agent capability, or pipeline variable. Revoke it after registration if organizational policy permits. The agent host needs Git, curl, Azure CLI, Docker Engine, Docker Compose v2, non-interactive sudo for the deployment commands, outbound HTTPS to GitHub/Azure DevOps/Azure/registries, and membership in the `docker` group. Docker and Compose are already present on the target host.

Grant the `Search Api` project permission to use `OMetaSearchPool`. Restrict the pool to this target if other projects do not need it. In the `emailvalidation-production` Azure DevOps environment, add the desired approver under **Approvals and checks** before enabling production runs.

## One-time host preparation

Create `/opt/emailvalidation/.env` outside source control. It supplies production configuration and paths to root-readable source files for Docker secrets:

```dotenv
AZURE_TENANT_ID=<tenant-id>
AZURE_CLIENT_ID=<client-id>
AUTHORITY=https://<token-issuer>/
AUDIENCE=emailvalidation-api
AZURE_APPCONFIG_SECRET_FILE=/etc/emailvalidation/app-configuration-connection-string
AZURE_CLIENT_CERTIFICATE_FILE=/etc/emailvalidation/azure-client-certificate.pfx
```

The pipeline installs only versioned manifests beneath `/opt/emailvalidation`; it never overwrites `.env` or the two source secret files. It obtains a short-lived ACR access token from the existing Azure service connection, feeds it to `docker login` through standard input, pulls the exact Git SHA tag, and logs Docker out after deployment.

Before the first production run, verify:

```bash
getent ahostsv4 email.digitalwarehouse.io
sudo ss -lntp | grep -E ':(80|443|8080|8081)\b' || true
sudo test -r /opt/emailvalidation/.env
```

The first run must enable the certificate-bootstrap parameter. Later releases leave it disabled; the Certbot service performs renewal checks automatically.
