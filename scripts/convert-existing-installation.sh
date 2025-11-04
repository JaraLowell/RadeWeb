#!/bin/bash

# Simple script to change ownership of existing RadeWeb installation to radeweb user
# Run this from your existing RadeWeb directory (e.g., /srv/RadeWeb)

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}This script must be run as root (use sudo)${NC}"
    exit 1
fi

RADEWEB_USER="radeweb"
CURRENT_DIR="$(pwd)"

echo -e "${BLUE}Converting existing RadeWeb installation to run as $RADEWEB_USER user${NC}"
echo "Current directory: $CURRENT_DIR"
echo

# Create user if it doesn't exist
if ! id "$RADEWEB_USER" &>/dev/null; then
    echo -e "${YELLOW}Creating user: $RADEWEB_USER${NC}"
    useradd --system --shell /bin/false --home-dir "$CURRENT_DIR" "$RADEWEB_USER"
    echo -e "${GREEN}User created${NC}"
else
    echo -e "${GREEN}User $RADEWEB_USER already exists${NC}"
fi

# Stop any running processes
echo -e "${YELLOW}Stopping any running RadeWeb processes...${NC}"
pkill -f "dotnet.*RadegastWeb" || true
pkill -f "dotnet run" || true

# Ensure required directories exist
echo -e "${YELLOW}Ensuring directory structure...${NC}"
mkdir -p "$CURRENT_DIR/data"
mkdir -p "$CURRENT_DIR/logs"
mkdir -p "$CURRENT_DIR/certificates"

# Change ownership
echo -e "${YELLOW}Changing ownership to $RADEWEB_USER...${NC}"
chown -R "$RADEWEB_USER:$RADEWEB_USER" "$CURRENT_DIR"

# Set proper permissions
echo -e "${YELLOW}Setting permissions...${NC}"
chmod -R 755 "$CURRENT_DIR"
chmod -R 750 "$CURRENT_DIR/data"
chmod -R 750 "$CURRENT_DIR/logs"

# Secure certificates
if [ -d "$CURRENT_DIR/certificates" ]; then
    chmod 700 "$CURRENT_DIR/certificates"
    chmod 600 "$CURRENT_DIR/certificates"/*.pfx 2>/dev/null || true
    chmod 600 "$CURRENT_DIR/certificates"/*.key 2>/dev/null || true
fi

# Install systemd service if available
if [ -f "$CURRENT_DIR/scripts/radeweb.service" ]; then
    echo -e "${YELLOW}Installing systemd service...${NC}"
    cp "$CURRENT_DIR/scripts/radeweb.service" /etc/systemd/system/
    
    # Update WorkingDirectory in service file to current directory
    sed -i "s|WorkingDirectory=/srv/RadeWeb|WorkingDirectory=$CURRENT_DIR|g" /etc/systemd/system/radeweb.service
    sed -i "s|ExecStart=/usr/bin/dotnet run --project /srv/RadeWeb/RadegastWeb.csproj|ExecStart=/usr/bin/dotnet run --project $CURRENT_DIR/RadegastWeb.csproj|g" /etc/systemd/system/radeweb.service
    sed -i "s|ReadWritePaths=/srv/RadeWeb|ReadWritePaths=$CURRENT_DIR|g" /etc/systemd/system/radeweb.service
    
    systemctl daemon-reload
    systemctl enable radeweb
    echo -e "${GREEN}Service installed and enabled${NC}"
fi

echo -e "${GREEN}Conversion completed successfully!${NC}"
echo
echo -e "${BLUE}Summary:${NC}"
echo "  User:         $RADEWEB_USER"
echo "  Directory:    $CURRENT_DIR"
echo "  Data:         $CURRENT_DIR/data"
echo "  Logs:         $CURRENT_DIR/logs"
echo "  Certificates: $CURRENT_DIR/certificates"
echo
echo -e "${BLUE}Next steps:${NC}"
echo "1. Test manually: sudo -u $RADEWEB_USER dotnet run"
echo "2. Start service: sudo systemctl start radeweb"
echo "3. Check status:  sudo systemctl status radeweb"
echo "4. View logs:     sudo journalctl -u radeweb -f"