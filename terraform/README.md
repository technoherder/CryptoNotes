# CryptoNotes Terraform Deployment

Automates provisioning of CryptoNotes infrastructure on AWS Lightsail.

## What Gets Created

- Lightsail instance (Debian 12)
- Static IP address
- Firewall rules (SSH, HTTP, HTTPS)
- (Optional) DNS zone and A record

## Prerequisites

1. [Terraform](https://terraform.io) installed
2. AWS credentials configured (`aws configure` or environment variables)
3. (Optional) SSH key pair created in Lightsail console

## Usage

```bash
# Initialize Terraform
cd terraform
terraform init

# Preview changes
terraform plan -var="domain=talk.example.com"

# Apply
terraform apply -var="domain=talk.example.com"

# Or use a tfvars file
cp variables.tfvars.example terraform.tfvars
# Edit terraform.tfvars
terraform apply
```

## Post-Deployment

1. **Add DNS A record** (if not using Terraform-managed DNS):
   ```
   talk.example.com -> <static_ip from output>
   ```

2. **Wait for setup to complete** (~3-5 minutes)

3. **SSH into the server**:
   ```bash
   ssh admin@<static_ip>
   ```

4. **Upload or build the Docker image**:
   ```bash
   # Option A: Build locally on server
   cd /opt/cryptonotes
   # Copy source code, then:
   docker build -t cryptonotes-server -f docker/Dockerfile .

   # Option B: Pull from registry (if published)
   docker pull ghcr.io/youruser/cryptonotes:latest
   ```

5. **Start the service**:
   ```bash
   cd /opt/cryptonotes
   docker compose up -d
   ```

6. **Verify**:
   ```bash
   curl https://talk.example.com/health
   # Should return: {"status":"healthy",...}
   ```

## Outputs

After `terraform apply`, you'll see:

- `public_ip` - Static IP address
- `ssh_command` - Ready-to-use SSH command
- `server_url` - HTTPS URL for the server
- `next_steps` - Manual steps to complete deployment

## Destroying

```bash
terraform destroy -var="domain=talk.example.com"
```

## Cost

- Nano instance: ~$5/month
- Static IP: Free (while attached)
- DNS zone: $0.50/month (if created)
