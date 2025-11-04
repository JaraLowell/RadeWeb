#!/bin/bash

# Script to migrate existing RadeWeb installation from root to radeweb user
# Run this script as root after running setup-radeweb-user.sh

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}Migrating RadeWeb from root to radeweb user${NC}"

# Configuration
RADEWEB_USER="radeweb"
RADEWEB_GROUP="radeweb"
CURRENT_APP_DIR="$(pwd)"
TARGET_APP_DIR="/srv/RadeWeb"

# Stop the application if it's running
echo -e "${YELLOW}Stopping RadeWeb application...${NC}"
if systemctl is-active --quiet radeweb 2>/dev/null; then
    systemctl stop radeweb
    echo -e "${GREEN}Application stopped${NC}"
else
    echo -e "${YELLOW}Application service not found or already stopped${NC}"
fi

# Kill any running dotnet processes for RadeWeb
pkill -f "dotnet.*RadegastWeb" || true

# If we're not already in the target directory, copy files
if [ "$CURRENT_APP_DIR" != "$TARGET_APP_DIR" ]; then
    echo -e "${YELLOW}Copying application files to $TARGET_APP_DIR...${NC}"
    mkdir -p "$TARGET_APP_DIR"
    cp -r "$CURRENT_APP_DIR"/* "$TARGET_APP_DIR/"
else
    echo -e "${GREEN}Already in target directory: $TARGET_APP_DIR${NC}"
fi

# Set correct ownership and permissions
echo -e "${YELLOW}Setting ownership and permissions...${NC}"

# Give radeweb user ownership of entire application directory
chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$TARGET_APP_DIR"
chmod -R 755 "$TARGET_APP_DIR"

# Ensure data subdirectories have proper permissions
if [ -d "$TARGET_APP_DIR/data" ]; then
    chmod -R 750 "$TARGET_APP_DIR/data"
fi

# Ensure logs subdirectory has proper permissions  
if [ -d "$TARGET_APP_DIR/logs" ]; then
    chmod -R 750 "$TARGET_APP_DIR/logs"
fi

# Set proper permissions for certificates (if they exist)
if [ -d "$TARGET_APP_DIR/certificates" ]; then
    chmod 700 "$TARGET_APP_DIR/certificates"
    chmod 600 "$TARGET_APP_DIR/certificates"/*.pfx 2>/dev/null || true
    chmod 600 "$TARGET_APP_DIR/certificates"/*.key 2>/dev/null || true
fi

# Make any executable files runnable
chmod +x "$TARGET_APP_DIR/RadegastWeb" 2>/dev/null || true

echo -e "${GREEN}Migration completed!${NC}"
echo -e "${BLUE}Next steps:${NC}"
echo "1. Install systemd service file: sudo cp scripts/radeweb.service /etc/systemd/system/"
echo "2. Reload systemd: sudo systemctl daemon-reload"
echo "3. Enable service: sudo systemctl enable radeweb"
echo "4. Start service: sudo systemctl start radeweb"
echo "5. Check status: sudo systemctl status radeweb"