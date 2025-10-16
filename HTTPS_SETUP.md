# HTTPS/SSL Certificate Setup Guide

This guide explains how to configure SSL certificates for the Radegast Web server.

## Current Configuration

The server is configured to run on:
- HTTP: `http://*:15269`
- HTTPS: `https://*:15277`

## Certificate Configuration Methods

### Method 1: Using appsettings.json (Recommended)

The certificate is configured in `appsettings.json` under the `Kestrel:Endpoints` section:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://*:15277",
        "Certificate": {
          "Path": "./certificates/radegastweb.pfx",
          "Password": "YourCertificatePassword"
        }
      }
    }
  }
}
```

### Method 2: Using Environment Variables

Set the following environment variables:
```powershell
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = "certificates/radegastweb.pfx"
$env:ASPNETCORE_Kestrel__Certificates__Default__Password = "YourCertificatePassword"
```

### Method 3: Using User Secrets (Development)

For development environments, use user secrets to store the certificate password:

```powershell
# Initialize user secrets
dotnet user-secrets init

# Set the certificate password
dotnet user-secrets set "Kestrel:Endpoints:Https:Certificate:Password" "YourCertificatePassword"
```

Then update `appsettings.Development.json`:
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Certificate": {
          "Path": "./certificates/dev-cert.pfx"
        }
      }
    }
  }
}
```

## Certificate Types and Sources

### 1. Development Certificates

#### Windows (.NET Development Certificate)
```powershell
# Generate and trust development certificate
dotnet dev-certs https --export-path ./certificates/dev-cert.pfx --password "dev-password" --format pfx

# Trust the certificate (Windows)
dotnet dev-certs https --trust
```

#### Linux/macOS (OpenSSL Development Certificate)
```bash
# Create development certificate with OpenSSL
openssl req -x509 -newkey rsa:2048 -keyout ./certificates/dev-cert.key -out ./certificates/dev-cert.crt -days 365 -nodes -subj "/CN=localhost"

# Convert to PFX format
openssl pkcs12 -export -out ./certificates/dev-cert.pfx -inkey ./certificates/dev-cert.key -in ./certificates/dev-cert.crt -password pass:dev-password

# Clean up individual files
rm ./certificates/dev-cert.key ./certificates/dev-cert.crt

# For Linux, add certificate to trust store (optional, for browser trust)
sudo cp ./certificates/dev-cert.crt /usr/local/share/ca-certificates/radegast-dev.crt
sudo update-ca-certificates
```

#### Cross-Platform Development with .NET
```bash
# This works on Linux/macOS/Windows
dotnet dev-certs https --export-path ./certificates/dev-cert.pfx --password "dev-password" --format pfx

# Trust certificate (Linux/macOS - may require manual browser import)
dotnet dev-certs https --trust
```

### 2. Let's Encrypt (Free)

#### Windows
```powershell
# Install certbot
winget install Certbot.Certbot

# Generate certificate (replace yourdomain.com)
certbot certonly --standalone -d yourdomain.com

# Convert to PFX format
openssl pkcs12 -export -out ./certificates/radegastweb.pfx -inkey /path/to/privkey.pem -in /path/to/cert.pem -certfile /path/to/chain.pem
```

#### Linux
```bash
# Install certbot (Ubuntu/Debian)
sudo apt update
sudo apt install certbot

# Or for CentOS/RHEL/Fedora
sudo dnf install certbot

# Generate certificate (replace yourdomain.com)
sudo certbot certonly --standalone -d yourdomain.com

# Convert to PFX format (certificates are in /etc/letsencrypt/live/yourdomain.com/)
sudo openssl pkcs12 -export -out ./certificates/radegastweb.pfx \
  -inkey /etc/letsencrypt/live/yourdomain.com/privkey.pem \
  -in /etc/letsencrypt/live/yourdomain.com/cert.pem \
  -certfile /etc/letsencrypt/live/yourdomain.com/chain.pem \
  -password pass:YourStrongPassword

# Set proper permissions
sudo chown $USER:$USER ./certificates/radegastweb.pfx
chmod 600 ./certificates/radegastweb.pfx
```

#### Automated Let's Encrypt Renewal (Linux)
```bash
# Create renewal script
cat > /usr/local/bin/radegast-cert-renew.sh << 'EOF'
#!/bin/bash
DOMAIN="yourdomain.com"
CERT_PATH="/etc/letsencrypt/live/$DOMAIN"
APP_CERT_PATH="/path/to/your/app/certificates/radegastweb.pfx"
CERT_PASSWORD="YourStrongPassword"

# Renew certificate
certbot renew --quiet

# Convert to PFX if renewal was successful
if [ -f "$CERT_PATH/cert.pem" ]; then
    openssl pkcs12 -export -out "$APP_CERT_PATH" \
        -inkey "$CERT_PATH/privkey.pem" \
        -in "$CERT_PATH/cert.pem" \
        -certfile "$CERT_PATH/chain.pem" \
        -password pass:$CERT_PASSWORD
    
    # Restart your application (adjust command as needed)
    systemctl restart radegastweb
fi
EOF

# Make script executable
chmod +x /usr/local/bin/radegast-cert-renew.sh

# Add to crontab for automatic renewal (runs twice daily)
echo "0 */12 * * * /usr/local/bin/radegast-cert-renew.sh" | sudo crontab -
```

### 3. Commercial Certificate

If you have a commercial certificate:

1. Ensure it's in PFX format
2. Place it in the `./certificates/` directory
3. Update the path and password in configuration

### 4. Self-Signed Certificate (Production)

#### Windows (PowerShell)
```powershell
# Create certificate
$cert = New-SelfSignedCertificate -DnsName "localhost", "yourdomain.com" -CertStoreLocation "cert:\LocalMachine\My" -KeyUsage KeyEncipherment,DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -NotAfter (Get-Date).AddYears(2)

# Export to PFX
$password = ConvertTo-SecureString -String "YourStrongPassword" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "./certificates/radegastweb.pfx" -Password $password
```

#### Linux/macOS (OpenSSL)
```bash
# Create private key
openssl genrsa -out ./certificates/radegastweb.key 2048

# Create certificate signing request (CSR)
openssl req -new -key ./certificates/radegastweb.key -out ./certificates/radegastweb.csr -subj "/C=US/ST=State/L=City/O=Organization/OU=OrgUnit/CN=localhost"

# Create self-signed certificate (valid for 2 years)
openssl x509 -req -days 730 -in ./certificates/radegastweb.csr -signkey ./certificates/radegastweb.key -out ./certificates/radegastweb.crt

# Convert to PFX format for ASP.NET Core
openssl pkcs12 -export -out ./certificates/radegastweb.pfx -inkey ./certificates/radegastweb.key -in ./certificates/radegastweb.crt -password pass:YourStrongPassword

# Clean up temporary files
rm ./certificates/radegastweb.csr
```

#### Advanced OpenSSL with Subject Alternative Names (SAN)
```bash
# Create a config file for SAN certificate
cat > ./certificates/san.conf << EOF
[req]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
req_extensions = v3_req

[dn]
C=US
ST=State
L=City
O=Organization
OU=OrgUnit
CN=localhost

[v3_req]
basicConstraints = CA:FALSE
keyUsage = nonRepudiation, digitalSignature, keyEncipherment
subjectAltName = @alt_names

[alt_names]
DNS.1 = localhost
DNS.2 = yourdomain.com
DNS.3 = *.yourdomain.com
IP.1 = 127.0.0.1
IP.2 = ::1
EOF

# Generate private key
openssl genrsa -out ./certificates/radegastweb.key 2048

# Generate certificate with SAN
openssl req -new -x509 -key ./certificates/radegastweb.key -out ./certificates/radegastweb.crt -days 730 -config ./certificates/san.conf -extensions v3_req

# Convert to PFX
openssl pkcs12 -export -out ./certificates/radegastweb.pfx -inkey ./certificates/radegastweb.key -in ./certificates/radegastweb.crt -password pass:YourStrongPassword

# Clean up
rm ./certificates/san.conf
```

## Linux/OpenSSL Specific Instructions

### Quick Development Setup (Linux)
```bash
# One-liner for development certificate
openssl req -x509 -newkey rsa:2048 -keyout temp.key -out temp.crt -days 365 -nodes -subj "/CN=localhost" && \
openssl pkcs12 -export -out ./certificates/dev-cert.pfx -inkey temp.key -in temp.crt -password pass:dev-password && \
rm temp.key temp.crt
```

### Production Certificate with Multiple Domains
```bash
# Create configuration file for multiple domains
cat > cert.conf << EOF
[req]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
req_extensions = v3_req

[dn]
C=US
ST=California
L=San Francisco
O=Your Organization
OU=IT Department
CN=yourdomain.com

[v3_req]
basicConstraints = CA:FALSE
keyUsage = nonRepudiation, digitalSignature, keyEncipherment
subjectAltName = @alt_names

[alt_names]
DNS.1 = yourdomain.com
DNS.2 = www.yourdomain.com
DNS.3 = api.yourdomain.com
DNS.4 = localhost
IP.1 = 127.0.0.1
EOF

# Generate the certificate
openssl req -new -x509 -key <(openssl genrsa 2048) -out temp.crt -days 730 -config cert.conf -extensions v3_req
openssl rsa -in temp.key -out final.key
openssl pkcs12 -export -out ./certificates/radegastweb.pfx -inkey final.key -in temp.crt -password pass:YourStrongPassword

# Cleanup
rm cert.conf temp.crt temp.key final.key
```

### Certificate Validation and Information
```bash
# View certificate information
openssl pkcs12 -info -in ./certificates/radegastweb.pfx -noout

# Extract and view certificate details
openssl pkcs12 -in ./certificates/radegastweb.pfx -clcerts -nokeys -out temp.crt
openssl x509 -in temp.crt -text -noout
rm temp.crt

# Test certificate validity
openssl pkcs12 -in ./certificates/radegastweb.pfx -noout -passin pass:YourPassword
```

### Converting Between Certificate Formats
```bash
# PEM to PFX
openssl pkcs12 -export -out certificate.pfx -inkey private.key -in certificate.crt

# PFX to PEM
openssl pkcs12 -in certificate.pfx -out certificate.pem -nodes

# Extract private key from PFX
openssl pkcs12 -in certificate.pfx -nocerts -out private.key -nodes

# Extract certificate from PFX
openssl pkcs12 -in certificate.pfx -clcerts -nokeys -out certificate.crt
```

### File Permissions and Security (Linux)
```bash
# Set secure permissions for certificate files
chmod 600 ./certificates/*.pfx
chmod 600 ./certificates/*.key
chmod 644 ./certificates/*.crt

# Change ownership to application user
sudo chown appuser:appuser ./certificates/*

# Create certificate directory with proper permissions
sudo mkdir -p /etc/radegast/certificates
sudo chown appuser:appuser /etc/radegast/certificates
sudo chmod 750 /etc/radegast/certificates
```

## Certificate Deployment

### Development Environment

1. Use `appsettings.Development.json` for development-specific settings
2. Store passwords in user secrets
3. Use development certificates

### Production Environment

1. Use environment variables or Azure Key Vault for secrets
2. Ensure certificate files have proper permissions
3. Use commercial or Let's Encrypt certificates

### Docker Deployment

For Docker containers, mount the certificate directory:

```yaml
version: '3.8'
services:
  radegastweb:
    build: .
    ports:
      - "15269:15269"
      - "15277:15277"
    volumes:
      - ./certificates:/app/certificates:ro
    environment:
      - ASPNETCORE_Kestrel__Certificates__Default__Path=/app/certificates/radegastweb.pfx
      - ASPNETCORE_Kestrel__Certificates__Default__Password=YourPassword
```

## Security Best Practices

1. **Never commit certificates to source control**
   - Add `*.pfx` to `.gitignore`
   - Use separate certificates for different environments

2. **Use strong passwords**
   - Generate random passwords for certificate files
   - Store passwords securely (Key Vault, user secrets, etc.)

3. **File permissions**
   ```powershell
   # Set read-only permissions for certificate files
   icacls "./certificates/radegastweb.pfx" /grant:r "IIS_IUSRS:R"
   ```

4. **Certificate rotation**
   - Monitor certificate expiration dates
   - Implement automated renewal for Let's Encrypt
   - Test certificate updates in staging first

## Troubleshooting

### Common Issues

1. **Certificate password errors**
   - Verify the password in configuration matches the PFX file
   - Check for special characters that need escaping

2. **File path issues**
   - Use relative paths from the application root
   - Ensure the certificate file exists and is readable

3. **Trust issues**
   - For development, run `dotnet dev-certs https --trust`
   - For self-signed certificates, add to Windows certificate store

### Verification

Test your HTTPS configuration:

```powershell
# Check if HTTPS endpoint is working
Invoke-WebRequest -Uri "https://localhost:15277" -SkipCertificateCheck

# View certificate details
Get-PfxCertificate -FilePath "./certificates/radegastweb.pfx"
```

## Environment-Specific Configuration

### appsettings.Development.json
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Certificate": {
          "Path": "./certificates/dev-cert.pfx"
        }
      }
    }
  }
}
```

### appsettings.Production.json
```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Certificate": {
          "Path": "./certificates/radegastweb.pfx"
        }
      }
    }
  }
}
```

Remember to set the password via environment variables or user secrets for each environment.