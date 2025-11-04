#!/bin/bash

# Complete deployment script for RadeWeb
# This script handles the entire migration from root to radeweb user

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}This script must be run as root${NC}"
    exit 1
fi

echo -e "${BLUE}RadeWeb User Migration and Deployment Script${NC}"
echo "This script will:"
echo "1. Create a dedicated radeweb user"
echo "2. Set up proper directory structure"
echo "3. Migrate existing files"
echo "4. Configure systemd service"
echo "5. Set up proper permissions"
echo

read -p "Continue? (y/N): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 1
fi

# Configuration
RADEWEB_USER="radeweb"
RADEWEB_GROUP="radeweb"
CURRENT_DIR="$(pwd)"
APP_DIR="/srv/RadeWeb"

echo -e "${YELLOW}Step 1: Creating user and directories...${NC}"

# Create user if it doesn't exist
if ! id "$RADEWEB_USER" &>/dev/null; then
    useradd --system --shell /bin/false --home-dir "$DATA_DIR" --create-home "$RADEWEB_USER"
    echo -e "${GREEN}Created user: $RADEWEB_USER${NC}"
else
    echo -e "${GREEN}User $RADEWEB_USER already exists${NC}"
fi

# Create directories
mkdir -p "$APP_DIR"
mkdir -p "$APP_DIR/data" "$APP_DIR/logs" "$APP_DIR/certificates"

echo -e "${YELLOW}Step 2: Stopping existing services...${NC}"

# Stop the application if running
systemctl stop radeweb 2>/dev/null || true
pkill -f "dotnet.*RadegastWeb" || true

echo -e "${YELLOW}Step 3: Setting up application directory...${NC}"

# If we're not already in the target directory, copy/move files
if [ "$CURRENT_DIR" != "$APP_DIR" ]; then
    echo "Copying application files to $APP_DIR..."
    cp -r "$CURRENT_DIR"/* "$APP_DIR/"
else
    echo "Already in target directory: $APP_DIR"
fi

echo -e "${YELLOW}Step 4: Ensuring directory structure...${NC}"

# Ensure required subdirectories exist
mkdir -p "$APP_DIR/data" "$APP_DIR/logs" "$APP_DIR/certificates"

# Copy production config template if it doesn't exist
if [ ! -f "$APP_DIR/appsettings.Production.json" ] && [ -f "$APP_DIR/scripts/appsettings.Production.json" ]; then
    echo "Copying production configuration template..."
    cp "$APP_DIR/scripts/appsettings.Production.json" "$APP_DIR/"
fi

echo -e "${YELLOW}Step 5: Setting permissions...${NC}"

# Give radeweb user ownership of entire application directory
chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$APP_DIR"
chmod -R 755 "$APP_DIR"

# Ensure data subdirectory has proper permissions
chmod -R 750 "$APP_DIR/data"

# Ensure logs subdirectory has proper permissions
chmod -R 750 "$APP_DIR/logs"

# Secure certificate files
if [ -d "$APP_DIR/certificates" ]; then
    chmod 700 "$APP_DIR/certificates"
    chmod 600 "$APP_DIR/certificates"/*.pfx 2>/dev/null || true
    chmod 600 "$APP_DIR/certificates"/*.key 2>/dev/null || true
fi

echo -e "${YELLOW}Step 6: Installing systemd service...${NC}"

# Copy systemd service file
if [ -f "$APP_DIR/scripts/radeweb.service" ]; then
    cp "$APP_DIR/scripts/radeweb.service" /etc/systemd/system/
    systemctl daemon-reload
    systemctl enable radeweb
    echo -e "${GREEN}Systemd service installed and enabled${NC}"
fi

echo -e "${GREEN}Migration completed successfully!${NC}"
echo
echo -e "${BLUE}Summary:${NC}"
echo "  User:         $RADEWEB_USER"
echo "  Application:  $APP_DIR"
echo "  Data:         $APP_DIR/data"
echo "  Logs:         $APP_DIR/logs" 
echo "  Certificates: $APP_DIR/certificates"
echo "  User accounts: $APP_DIR/data/accounts/{account-uuid}/"
echo
echo -e "${BLUE}Next steps:${NC}"
echo "1. Review configuration in $CONFIG_DIR/appsettings.Production.json"
echo "2. Start the service: systemctl start radeweb"
echo "3. Check status: systemctl status radeweb"
echo "4. View logs: journalctl -u radeweb -f"
echo
echo -e "${YELLOW}Security notes:${NC}"
echo "- Application runs as non-root user 'radeweb'"
echo "- Application directory owned by radeweb user"
echo "- Service has restricted system access"  
echo "- User account data isolated in separate subdirectories"
echo "- Consider setting up a reverse proxy (nginx/apache) for production"