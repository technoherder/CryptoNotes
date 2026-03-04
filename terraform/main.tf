# =============================================================================
# CryptoNotes - AWS Lightsail Terraform Configuration
#
# Provisions:
#   - Lightsail instance (Debian)
#   - Static IP
#   - DNS zone and records
#   - Firewall rules
#
# Usage:
#   terraform init
#   terraform plan -var="domain=talk.technoherder.com"
#   terraform apply -var="domain=talk.technoherder.com"
# =============================================================================

terraform {
  required_version = ">= 1.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
}

provider "aws" {
  region = var.aws_region
}

# =============================================================================
# Variables
# =============================================================================

variable "aws_region" {
  description = "AWS region for Lightsail"
  type        = string
  default     = "us-east-1"
}

variable "domain" {
  description = "Domain name for CryptoNotes (e.g., talk.example.com)"
  type        = string
}

variable "instance_name" {
  description = "Name for the Lightsail instance"
  type        = string
  default     = "cryptonotes-server"
}

variable "blueprint_id" {
  description = "Lightsail blueprint (OS image)"
  type        = string
  default     = "debian_12"
}

variable "bundle_id" {
  description = "Lightsail bundle (instance size)"
  type        = string
  default     = "nano_3_0"  # $5/mo: 512MB RAM, 1 vCPU, 20GB SSD
}

variable "ssh_key_name" {
  description = "Name of SSH key pair in Lightsail"
  type        = string
  default     = ""
}

variable "https_port" {
  description = "HTTPS port for the server (default: 443, use 1337 or custom port)"
  type        = number
  default     = 443
}

variable "create_dns_zone" {
  description = "Create a new DNS zone (false if using existing zone)"
  type        = bool
  default     = false
}

# =============================================================================
# Lightsail Instance
# =============================================================================

resource "aws_lightsail_instance" "cryptonotes" {
  name              = var.instance_name
  availability_zone = "${var.aws_region}a"
  blueprint_id      = var.blueprint_id
  bundle_id         = var.bundle_id
  key_pair_name     = var.ssh_key_name != "" ? var.ssh_key_name : null

  user_data = base64encode(templatefile("${path.module}/user-data.sh", {
    domain     = var.domain
    https_port = var.https_port
  }))

  tags = {
    Name        = var.instance_name
    Application = "CryptoNotes"
    ManagedBy   = "Terraform"
  }
}

# =============================================================================
# Static IP
# =============================================================================

resource "aws_lightsail_static_ip" "cryptonotes" {
  name = "${var.instance_name}-ip"
}

resource "aws_lightsail_static_ip_attachment" "cryptonotes" {
  static_ip_name = aws_lightsail_static_ip.cryptonotes.name
  instance_name  = aws_lightsail_instance.cryptonotes.name
}

# =============================================================================
# Firewall Rules
# =============================================================================

resource "aws_lightsail_instance_public_ports" "cryptonotes" {
  instance_name = aws_lightsail_instance.cryptonotes.name

  port_info {
    protocol  = "tcp"
    from_port = 22
    to_port   = 22
  }

  port_info {
    protocol  = "tcp"
    from_port = 80
    to_port   = 80
  }

  port_info {
    protocol  = "tcp"
    from_port = var.https_port
    to_port   = var.https_port
  }

  port_info {
    protocol  = "udp"
    from_port = var.https_port
    to_port   = var.https_port
  }

  depends_on = [aws_lightsail_instance.cryptonotes]
}

# =============================================================================
# DNS Zone (optional - only if create_dns_zone = true)
# =============================================================================

resource "aws_lightsail_domain" "zone" {
  count       = var.create_dns_zone ? 1 : 0
  domain_name = regex("^[^.]+\\.(.+)$", var.domain)[0]  # Extract parent domain
}

# =============================================================================
# DNS A Record
# =============================================================================

resource "aws_lightsail_domain_entry" "cryptonotes" {
  count       = var.create_dns_zone ? 1 : 0
  domain_name = aws_lightsail_domain.zone[0].domain_name
  name        = split(".", var.domain)[0]  # subdomain part
  type        = "A"
  target      = aws_lightsail_static_ip.cryptonotes.ip_address
}

# =============================================================================
# Outputs
# =============================================================================

output "instance_name" {
  description = "Lightsail instance name"
  value       = aws_lightsail_instance.cryptonotes.name
}

output "public_ip" {
  description = "Static public IP address"
  value       = aws_lightsail_static_ip.cryptonotes.ip_address
}

output "domain" {
  description = "Domain configured for CryptoNotes"
  value       = var.domain
}

output "ssh_command" {
  description = "SSH command to connect to the server"
  value       = "ssh admin@${aws_lightsail_static_ip.cryptonotes.ip_address}"
}

output "server_url" {
  description = "CryptoNotes server URL"
  value       = var.https_port == 443 ? "https://${var.domain}" : "https://${var.domain}:${var.https_port}"
}

output "next_steps" {
  description = "Manual steps after provisioning"
  value       = <<-EOT

    1. If not using Terraform-managed DNS, add A record manually:
       ${var.domain} -> ${aws_lightsail_static_ip.cryptonotes.ip_address}

    2. Wait a few minutes for instance to complete setup

    3. SSH into the server and check deployment:
       ssh admin@${aws_lightsail_static_ip.cryptonotes.ip_address}
       sudo docker compose -f /opt/cryptonotes/docker-compose.yml logs -f

    4. Test the API:
       curl ${var.https_port == 443 ? "https://${var.domain}" : "https://${var.domain}:${var.https_port}"}/health
  EOT
}

output "https_port" {
  description = "HTTPS port configured"
  value       = var.https_port
}
