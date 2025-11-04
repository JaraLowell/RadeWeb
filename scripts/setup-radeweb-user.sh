#!/bin/bash

# Script to setup RadeWeb user and configure permissions
# Run this script as root to create the user and set up proper permissions

set -e  # Exit on any error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}Setting up RadeWeb user and permissions${NC}"

# Configuration
RADEWEB_USER="radeweb"
RADEWEB_GROUP="radeweb"
APP_DIR="/srv/RadeWeb"

# Create user and group if they don't exist
if ! id "$RADEWEB_USER" &>/dev/null; then
    echo -e "${YELLOW}Creating user: $RADEWEB_USER${NC}"
    useradd --system --shell /bin/false --home-dir "$DATA_DIR" --create-home "$RADEWEB_USER"
else
    echo -e "${GREEN}User $RADEWEB_USER already exists${NC}"
fi

# Create necessary directories if they don't exist
echo -e "${YELLOW}Setting up directories...${NC}"
mkdir -p "$APP_DIR"
mkdir -p "$APP_DIR/data"
mkdir -p "$APP_DIR/logs"
mkdir -p "$APP_DIR/certificates"

# Set ownership and permissions
echo -e "${YELLOW}Setting ownership and permissions...${NC}"

# Application directory - owned by radeweb user for full access
chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$APP_DIR"
chmod -R 755 "$APP_DIR"

# Ensure data subdirectory has proper permissions
chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$APP_DIR/data"
chmod -R 750 "$APP_DIR/data"

# Ensure logs subdirectory has proper permissions
chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$APP_DIR/logs"
chmod -R 750 "$APP_DIR/logs"

# Secure certificates if they exist
if [ -d "$APP_DIR/certificates" ]; then
    chown -R "$RADEWEB_USER:$RADEWEB_GROUP" "$APP_DIR/certificates"
    chmod -R 700 "$APP_DIR/certificates"
    chmod 600 "$APP_DIR/certificates"/*.pfx 2>/dev/null || true
    chmod 600 "$APP_DIR/certificates"/*.key 2>/dev/null || true
fi

echo -e "${GREEN}User setup completed!${NC}"
echo -e "${BLUE}Directory structure:${NC}"
echo "  Application: $APP_DIR"
echo "  Data:        $APP_DIR/data"
echo "  Logs:        $APP_DIR/logs"
echo "  Certificates: $APP_DIR/certificates"
echo "  User accounts: $APP_DIR/data/{account-uuid}/"