# EmailValidation host-local Ansible deployment

These playbooks provide auditable deployment verification and a break-glass host-local build path. They run only on the dedicated `esdata03-emailvalidation` Azure DevOps agent installed on the production host. The normal production path continues to publish immutable images through the repaired `OpenMeta Prod` service connection and Azure Container Registry.

The deployment:

- validates the complete `.162`–`.174` source-address, route, and firewall state;
- builds API, worker, and receive-only mail-forwarder images from the already-tested Git checkout;
- tags all three images with the full immutable Git SHA;
- preserves `/opt/emailvalidation/.env` and all secret source files;
- installs versioned Compose, Nginx, Certbot, identity configuration, and network-check artifacts;
- starts API, worker, and mail forwarder from the same revision;
- verifies Kestrel and the local TLS gateway;
- verifies catch-all acceptance, open-relay denial, and SMTP isolation from the outbound identity interface; and
- writes `/opt/emailvalidation/RELEASE` only after readiness succeeds.

The production pipeline uses `verify-host-local.yml` after its ACR deployment to prove that all three running
release containers and `/opt/emailvalidation/RELEASE` match the Git SHA. `deploy-host-local.yml` builds images named
`emailvalidation-local/emailvalidation-api:<sha>`, `emailvalidation-local/emailvalidation-worker:<sha>`, and
`emailvalidation-local/emailvalidation-mail-forwarder:<sha>` without pushing to a registry; reserve it for a reviewed
break-glass deployment when ACR is unavailable.

Syntax validation from the repository root:

```bash
ansible-playbook --syntax-check -i localhost, deploy/ansible/deploy-host-local.yml \
  -e emailvalidation_source_root="$PWD" \
  -e emailvalidation_release_tag=0000000000000000000000000000000000000000
ansible-playbook --syntax-check -i localhost, deploy/ansible/verify-host-local.yml \
  -e emailvalidation_release_tag=0000000000000000000000000000000000000000 \
  -e emailvalidation_image_registry=acrpometadsiscussrch.azurecr.io
```

The playbooks are intentionally host-local. Do not change their inventory to a public SSH target or expose administrative SSH to make deployment easier.
