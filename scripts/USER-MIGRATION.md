# RadeWeb User Migration Guide

This document provides step-by-step instructions for migrating your RadeWeb application from running as root to running under a dedicated `radeweb` user on Ubuntu.

## Overview

Running web applications as root is a security risk. This guide helps you create a dedicated user account and migrate your existing RadeWeb installation to run securely.

## Quick Migration (Automated)

For a fully automated migration, run:

```bash
# Make the script executable
chmod +x scripts/deploy-as-user.sh

# Run the deployment script as root
sudo ./scripts/deploy-as-user.sh
```

This will handle everything automatically. Skip to the "Verification" section if using this method.

## Manual Migration Steps

### 1. Create the RadeWeb User

```bash
# Create system user
sudo useradd --system --shell /bin/false --home-dir /var/lib/radeweb --create-home radeweb
```

### 2. Set Up Directory Structure

```bash
# Ensure the application directory exists
sudo mkdir -p /srv/RadeWeb
sudo mkdir -p /srv/RadeWeb/data
sudo mkdir -p /srv/RadeWeb/logs  
sudo mkdir -p /srv/RadeWeb/certificates
```

### 3. Migrate to Target Directory (if needed)

```bash
# If your application is currently elsewhere, copy it to /srv/RadeWeb
# If you're already in /srv/RadeWeb, skip this step
sudo cp -r /path/to/current/radeweb/* /srv/RadeWeb/
```

### 4. Set Permissions

```bash
# Give radeweb user ownership of entire application directory
sudo chown -R radeweb:radeweb /srv/RadeWeb
sudo chmod -R 755 /srv/RadeWeb

# Ensure data subdirectory has proper permissions
sudo chmod -R 750 /srv/RadeWeb/data

# Ensure logs subdirectory has proper permissions
sudo chmod -R 750 /srv/RadeWeb/logs

# Secure certificate files
sudo chmod 700 /srv/RadeWeb/certificates
sudo chmod 600 /srv/RadeWeb/certificates/*.pfx 2>/dev/null || true
sudo chmod 600 /srv/RadeWeb/certificates/*.key 2>/dev/null || true
```

### 5. Install Systemd Service

```bash
# Copy and enable service
sudo cp /srv/RadeWeb/scripts/radeweb.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable radeweb
```

## Directory Structure After Migration

```
/srv/RadeWeb/              # Application directory (owned by radeweb user)
├── RadegastWeb.csproj
├── Program.cs
├── appsettings.json
├── appsettings.Production.json
├── Controllers/
├── Services/
├── Models/
├── bin/
├── obj/
├── data/                  # Application data
│   ├── radegast.db
│   ├── accounts/
│   │   ├── {account-uuid-1}/
│   │   │   ├── cache/
│   │   │   └── logs/
│   │   └── {account-uuid-2}/
│   │       ├── cache/
│   │       └── logs/
│   └── cache/
├── logs/                  # Application logs
│   ├── radeweb-20241104.log
│   └── ...
├── certificates/          # SSL certificates
│   ├── radegastweb.pfx
│   └── ...
└── scripts/              # Deployment scripts
```

## Service Management

```bash
# Start the service
sudo systemctl start radeweb

# Check status
sudo systemctl status radeweb

# View logs
sudo journalctl -u radeweb -f

# Stop the service
sudo systemctl stop radeweb

# Restart the service
sudo systemctl restart radeweb
```

## Verification

1. **Check service status:**
   ```bash
   sudo systemctl status radeweb
   ```

2. **Verify it's running as radeweb user:**
   ```bash
   ps aux | grep RadegastWeb
   ```

3. **Check file permissions:**
   ```bash
   ls -la /srv/RadeWeb/
   ls -la /srv/RadeWeb/data/
   ls -la /srv/RadeWeb/logs/
   ```

4. **Test the application:**
   ```bash
   curl http://localhost:5000
   ```

## Security Benefits

- **Principle of Least Privilege**: Application runs with minimal permissions
- **Isolation**: Dedicated user account isolates the application
- **File System Security**: Proper ownership and permissions on all files
- **System Protection**: Limited access to system resources
- **Service Hardening**: Systemd security features enabled

## Troubleshooting

### Permission Errors

If you see permission errors:

```bash
# Check ownership
sudo ls -la /srv/RadeWeb/
sudo ls -la /srv/RadeWeb/data/
sudo ls -la /srv/RadeWeb/logs/

# Fix ownership if needed
sudo chown -R radeweb:radeweb /srv/RadeWeb/
```

### Service Won't Start

1. Check the service logs:
   ```bash
   sudo journalctl -u radeweb -n 50
   ```

2. Verify the application can run manually:
   ```bash
   cd /srv/RadeWeb
   sudo -u radeweb dotnet run
   ```

### Database Issues

If you have database permission issues:

```bash
# Ensure database is owned by radeweb user
sudo chown radeweb:radeweb /srv/RadeWeb/data/radegast.db
sudo chmod 660 /srv/RadeWeb/data/radegast.db
```

## Additional Security Considerations

1. **Firewall**: Configure UFW to limit access:
   ```bash
   sudo ufw allow 5000/tcp  # HTTP
   sudo ufw allow 5001/tcp  # HTTPS
   ```

2. **Reverse Proxy**: Consider using nginx or Apache as a reverse proxy

3. **SSL Certificates**: Use Let's Encrypt for production SSL certificates

4. **Regular Updates**: Keep the system and .NET runtime updated

5. **Monitoring**: Set up log monitoring and alerting

## Rollback

If you need to rollback to running as root:

1. Stop the service: `sudo systemctl stop radeweb`
2. Disable the service: `sudo systemctl disable radeweb`
3. Copy files back to your original directory
4. Run the application manually as root (not recommended)

## Notes

- The automated script (`deploy-as-user.sh`) handles all these steps
- Always backup your data before migration
- Test thoroughly in a non-production environment first
- Consider using Docker for even better isolation