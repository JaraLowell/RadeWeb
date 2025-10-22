# RadegastWeb - Multi-Account Second Life Web Client

RadegastWeb is a modern, web-based Second Life client inspired by the original Radegast client. It supports multiple concurrent accounts, each running in isolated threads with their own cache and log directories.

## 🌟 Features

- **Multi-Account Management**: Run multiple Second Life accounts simultaneously
- **Isolated Environments**: Each account has its own thread, cache, and logs
- **Real-time Communication**: Built with SignalR for instant updates
- **Web-Based Interface**: Access from any modern web browser
- **RESTful API**: Complete API for account and chat management
- **Responsive Design**: Works on desktop and mobile devices
- **Database Integration**: SQLite database with Entity Framework Core
- **Display Name Support**: Full display name resolution and management
- **Notice System**: Group notices and attachment handling
- **Presence Management**: Automatic presence updates and status tracking
- **Region Information**: Real-time region statistics and details
- **Dark Mode**: Toggle between light and dark themes
- **Chat History**: Persistent chat logging with database storage
- **Swagger API Documentation**: Complete API documentation and testing interface
- **AI Chat Bot Plugin**: Intelligent chat responses with configurable AI providers (OpenAI, Anthropic, local models)
- **Corrade Plugin**: Remote control via whisper commands for message relaying and bot functionality

## 🏗️ Architecture

- **Framework**: ASP.NET Core 8.0
- **Database**: SQLite with Entity Framework Core 9.0
- **Real-time**: SignalR with enhanced message handling
- **Protocol**: LibreMetaverse (OpenMetaverse) 2.4.10
- **Logging**: Serilog with structured logging and file rotation
- **Frontend**: Bootstrap 5 + Vanilla JavaScript with FontAwesome icons
- **Background Services**: Isolated background services for SL protocol events
- **Dependency Injection**: Full DI container with singleton and scoped services
- **Validation**: FluentValidation for input validation
- **Auto Mapping**: AutoMapper for object-to-object mapping

## 🚀 Quick Start

### Prerequisites

- .NET 8.0 SDK
  For linux
  ```powershell
  apt-get install -y dotnet-sdk-8.0
  ```
  see https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install
- Visual Studio Code (recommended)

### Running the Application

1. **Clone and Navigate**
   ```powershell
   git clone https://github.com/JaraLowell/RadeWeb.git
   ```

   ```powershell
   cd RadeWeb
   ```

2. **Restore Dependencies**
   ```powershell
   dotnet restore RadeWeb.sln
   ```

3. **Build the Project**
   ```powershell
   dotnet build RadeWeb.sln
   ```

4. **Update Database** (if upgrading from older version)
   ```powershell
   ./update-database.ps1
   ```
   Or manually:
   ```powershell
   dotnet ef database update --project RadegastWeb.csproj
   ```

5. **Run the Application**
   ```powershell
   dotnet run
   ```

6. **Open in Browser**
   - Main application: `http://localhost:15269`
   - Login page: `http://localhost:15269/login.html`
   - Statistics dashboard: `http://localhost:15269/stats.html`

### Using VS Code

1. Open the project in VS Code
2. Use `Ctrl+Shift+P` → "Tasks: Run Task" → "Run RadegastWeb"
3. Or press `F5` to start debugging

### Database

The application uses SQLite database stored in `./data/radegast.db`. The database is automatically created on first run with Entity Framework migrations.

## 📁 Project Structure

```
RadegastWeb/
├── Controllers/          # API Controllers
│   ├── AccountsController.cs     # Account management API
│   ├── AuthController.cs         # Authentication API
│   ├── ChatLogsController.cs     # Chat logging API
│   ├── CorradeController.cs      # Corrade plugin API
│   ├── GroupsController.cs       # Group management API
│   ├── PresenceController.cs     # Presence/status management
│   ├── RegionController.cs       # Region information API
│   ├── StatsController.cs        # Statistics API
│   └── TestController.cs         # Testing and debug API
├── Core/                # Core Second Life logic
│   └── WebRadegastInstance.cs    # Main SL client wrapper
├── Data/                # Database context and migrations
│   ├── RadegastDbContext.cs      # Entity Framework context
│   ├── DbContextFactory.cs      # Context factory
│   └── Migrations/               # EF database migrations
├── Hubs/                # SignalR Hubs
│   └── RadegastHub.cs           # Real-time communication hub
├── Models/              # Data models and DTOs
│   ├── Account.cs               # Account entity
│   ├── ChatMessage.cs           # Chat message entity
│   ├── DisplayName.cs           # Display name entity
│   ├── Notice.cs                # Group notice entity
│   ├── Dto.cs                   # Data transfer objects
│   ├── NoticeDto.cs             # Notice DTOs
│   └── RegionStatsDto.cs        # Region statistics DTOs
├── Services/            # Business logic services
│   ├── AccountService.cs        # Account management
│   ├── AiChatService.cs         # AI Chat Bot service
│   ├── AuthenticationService.cs # User authentication
│   ├── ChatHistoryService.cs    # Chat logging and history
│   ├── ChatLogService.cs        # Chat log management
│   ├── CorradeService.cs        # Corrade plugin service
│   ├── DisplayNameService.cs    # Display name resolution
│   ├── GlobalDisplayNameCache.cs # Global display name caching
│   ├── GroupService.cs          # Group management
│   ├── NameResolutionService.cs # Name resolution utilities
│   ├── NoticeService.cs         # Group notice handling
│   ├── PeriodicDisplayNameService.cs # Periodic name updates
│   ├── PresenceService.cs       # Presence management
│   ├── RadegastBackgroundService.cs # Background SL processing
│   ├── RegionInfoService.cs     # Region information
│   ├── RegionMapCacheService.cs # Region map caching
│   ├── SlUrlParser.cs           # SL URL parsing
│   └── StatsService.cs          # Statistics collection
├── wwwroot/             # Static web files
│   ├── css/
│   │   ├── main.css             # Main stylesheet
│   │   └── region-info.css      # Region info styling
│   ├── js/
│   │   ├── main.js              # Main application logic
│   │   ├── presence-client.js   # Presence management client
│   │   └── region-info.js       # Region info client
│   ├── index.html               # Main web interface
│   ├── login.html               # Authentication page
│   ├── corrade.html             # Corrade plugin management interface
│   └── stats.html               # Statistics dashboard
├── data/                # Runtime data
│   ├── radegast.db              # SQLite database
│   ├── aibot.json               # AI Chat Bot configuration
│   ├── AIBot_README.md          # AI Bot setup documentation
│   ├── corrade.json             # Corrade plugin configuration
│   ├── Corrade_README.md        # Corrade plugin documentation
│   └── accounts/                # Per-account data
│       └── {accountId}/
│           ├── cache/           # Asset cache
│           └── logs/            # Chat logs
├── logs/                # Application logs
│   └── radegast-web-{date}.log # Daily log files
├── bin/                 # Compiled binaries
├── obj/                 # Build artifacts
├── Properties/          # Project properties
└── Migrations/          # Entity Framework migrations
```

## 🔧 Configuration

### Database Configuration

RadegastWeb uses SQLite by default with Entity Framework Core:
- **Database Location**: `./data/radegast.db`
- **Migrations**: Automatic creation and updates
- **Supported Entities**: Accounts, ChatMessages, DisplayNames, Notices

### Grid Support

RadegastWeb supports multiple grids:
- **Second Life Main Grid** (Agni)
- **Second Life Beta Grid** (Aditi)  
- **Custom OpenSimulator Grids**

### Account Isolation

Each account gets:
- **Isolated Thread**: Independent processing with background services
- **Cache Directory**: `./data/accounts/{accountId}/cache/`
- **Log Files**: `./data/accounts/{accountId}/logs/`
- **Session State**: Separate connection state and database records
- **Display Names**: Cached display name resolution
- **Chat History**: Persistent chat logging

### Logging Configuration

Application uses Serilog with:
- **Console Output**: For development debugging
- **File Logging**: Daily rolling log files in `./logs/`
- **Structured Logging**: JSON-structured log entries
- **Log Levels**: Configurable via appsettings.json

## 📱 Usage

### Adding an Account

1. Click the "Add Account" button in the top-right corner
2. Enter your Second Life credentials:
   - **First Name**: Your avatar's first name
   - **Last Name**: Your avatar's last name
   - **Password**: Your account password
   - **Grid**: Select grid (Second Life, Beta Grid, or custom)
3. Save the account - it will appear in the accounts sidebar

### Managing Accounts

- **Login**: Select account and click "Login" to connect
- **Logout**: Click "Logout" to disconnect
- **Delete**: Remove account from the system
- **Status**: Real-time connection status updates

### Chat System

1. Select a connected account from the sidebar
2. Use the chat interface to:
   - **Send messages**: Type and press Enter or click Send
   - **Chat types**: Normal, Whisper, Shout
   - **View history**: Scroll through persistent chat history
   - **Real-time updates**: Receive messages instantly via SignalR

### Display Names

- Automatic display name resolution for all users
- Cached display names for performance
- Real-time updates when display names change

### Group Notices

- Receive group notices automatically
- View notice details and attachments
- Persistent storage of notice history

### Region Information

- View detailed region statistics
- Monitor region performance metrics
- Real-time updates of region data

### Presence Management

- Automatic presence detection
- Browser close detection sets accounts to "Away"
- Manual presence control

## 🔌 Plugin System

RadegastWeb includes two powerful plugin systems that extend functionality:

### AI Chat Bot Plugin

The AI Chat Bot plugin adds intelligent chat responses to your Second Life avatar. Features include:

- **Multiple AI Providers**: Support for OpenAI, Anthropic Claude, OpenRouter, and local Ollama models
- **Configurable Personality**: Customize system prompts and behavior
- **Smart Triggering**: Respond to name mentions, questions, or specific keywords
- **Chat Context**: Include recent chat history for contextual responses
- **Security Features**: UUID-based ignore lists and permission controls
- **Natural Delays**: Randomized response delays for realistic behavior
- **Resource Management**: Message size limits and history controls

**Configuration**: Edit `data/aibot.json` or see `data/AIBot_README.md` for detailed setup instructions.

**Quick Setup**:
1. Set your avatar name in the configuration
2. Add your AI provider API key
3. Configure response triggers and personality
4. Set `enabled: true` and restart RadegastWeb

### Corrade Plugin

The Corrade plugin enables remote control of your avatar through whisper commands, inspired by the Corrade bot system:

📝 This plugin is still under construction and more functions might be added.

- **Whisper Command Processing**: Execute commands via whispered messages
- **Multi-Entity Support**: Send messages to local chat, groups, or individual avatars
- **Group-Based Security**: Commands must be authorized by configured groups
- **Password Protection**: Each group requires a password for command execution
- **Permission System**: Fine-grained control over allowed message types
- **Web Management**: Full configuration through web interface at `/corrade.html`
- **Automatic Activation**: Plugin enables when groups are configured

**Configuration**: Use the web interface at `/corrade.html` or edit `data/corrade.json` directly. See `data/Corrade_README.md` for complete documentation.

**Command Examples**:
- Local chat: `command=tell&group=GROUP_UUID&password=PASS&entity=local&message=Hello!`
- Group message: `command=tell&group=GROUP_UUID&password=PASS&entity=group&message=Group hello!`
- Private message: `command=tell&group=GROUP_UUID&password=PASS&entity=avatar&target=AVATAR_UUID&message=Hi there!`

Both plugins are designed with security in mind and include comprehensive logging and error handling.

## 🔌 API Endpoints

> 📚 **Interactive API Documentation**: Visit `/swagger` for complete API documentation with testing capabilities (need be loged in)

### Authentication
- `POST /api/auth/login` - User login
- `POST /api/auth/logout` - User logout
- `GET /api/auth/status` - Check authentication status

### Accounts Management
- `GET /api/accounts` - List all accounts with status
- `POST /api/accounts` - Create new account
- `GET /api/accounts/{id}` - Get account details
- `DELETE /api/accounts/{id}` - Delete account
- `POST /api/accounts/{id}/login` - Login account
- `POST /api/accounts/{id}/logout` - Logout account
- `POST /api/accounts/{id}/chat` - Send chat message
- `GET /api/accounts/{id}/chat` - Get chat history
- `PUT /api/accounts/{id}/appearance` - Update avatar appearance

### Chat Logs Management
- `GET /api/chatlogs/{accountId}` - Get chat logs for account
- `GET /api/chatlogs/{accountId}/history` - Get chat history with pagination
- `DELETE /api/chatlogs/{accountId}` - Clear chat logs for account

### Groups Management
- `GET /api/groups/{accountId}` - Get groups for account
- `POST /api/groups/{accountId}/join` - Join a group
- `POST /api/groups/{accountId}/leave` - Leave a group
- `POST /api/groups/{accountId}/chat` - Send group message
- `GET /api/groups/{accountId}/{groupId}/notices` - Get group notices

### Presence Management
- `POST /api/presence/browser-close` - Handle browser close (set all to away)
- `POST /api/presence/{accountId}/status` - Update presence status
- `GET /api/presence/{accountId}` - Get current presence

### Region Information
- `GET /api/region/{accountId}/stats` - Get detailed region statistics
- `GET /api/region/{accountId}/info` - Get region information
- `POST /api/region/{accountId}/teleport` - Teleport to location

### Statistics
- `GET /api/stats/visitors` - Get visitor statistics
- `GET /api/stats/accounts` - Get account statistics
- `GET /api/stats/system` - Get system performance statistics

### Corrade Plugin (Authentication Required)
- `GET /api/corrade/status` - Get plugin status and configuration
- `GET /api/corrade/config` - Get current configuration (passwords hidden)
- `POST /api/corrade/config` - Update entire configuration
- `POST /api/corrade/config/groups` - Add new group configuration
- `DELETE /api/corrade/config/groups/{groupUuid}` - Remove group configuration
- `POST /api/corrade/test-command` - Test command syntax without execution

### Display Names
- `GET /api/displaynames/{accountId}` - Get cached display names
- `POST /api/displaynames/{accountId}/resolve` - Resolve specific display name

### Group Notices
- `GET /api/notices/{accountId}` - Get group notices
- `GET /api/notices/{accountId}/{noticeId}` - Get specific notice details

### Testing & Debug
- `GET /api/test/ping` - Health check endpoint
- `GET /api/test/auth` - Test authentication
- `POST /api/test/simulate` - Simulate various test scenarios

### Real-time Hub
- `/radegasthub` - SignalR hub for real-time events:
  - Chat messages
  - Login/logout status
  - Presence updates
  - Region information
  - Group notices
  - Display name updates

### API Documentation
- `/swagger` - Interactive Swagger/OpenAPI documentation
- `/swagger/v1/swagger.json` - OpenAPI JSON specification

## 🛠️ Development

### Prerequisites
- **.NET 8.0 SDK** or later
- **Visual Studio Code** with C# extension (recommended)
- **Git** for version control

### Building
```powershell
dotnet build RadeWeb.sln
```

### Running Tests
```powershell
dotnet test
```

### Debugging
Use VS Code's built-in debugger or:
```powershell
dotnet run --environment Development
```

### Hot Reload (Development)
```powershell
dotnet watch run
```

### Database Management

#### Create Migration
```powershell
dotnet ef migrations add MigrationName
```

#### Update Database
```powershell
dotnet ef database update
```

#### Apply Database Migrations (for updates)
When updating to a new version, apply any pending migrations:
```powershell
./update-database.ps1
```

#### Reset Database
```powershell
Remove-Item ./data/radegast.db
dotnet run
```

### Project Configuration

#### Development Settings
- Located in `appsettings.Development.json`
- Swagger UI enabled
- Detailed error messages
- Console and file logging

#### Production Settings
- Located in `appsettings.json`
- Optimized logging levels
- Security headers enabled

### Code Structure Guidelines

1. **Controllers**: Thin controllers with minimal logic
2. **Services**: Business logic and SL protocol handling
3. **Models**: Data entities and DTOs
4. **Background Services**: Long-running SL protocol tasks
5. **Dependency Injection**: All services registered in Program.cs

## 🔐 Security Considerations

- **Password Security**: Passwords are stored in memory only during runtime
- **Account Isolation**: Each account runs in complete isolation
- **No Persistent Passwords**: No password storage in database or files
- **Local-only Default**: Configured for localhost access (configure CORS for remote)
- **HTTPS Support**: SSL/TLS encryption available (port 7077)
- **Input Validation**: FluentValidation for all user inputs
- **SQL Injection Protection**: Entity Framework with parameterized queries
- **XSS Protection**: Proper output encoding in web interface

## 🏗️ Technical Features

### Entity Framework Integration
- **Code-First**: Database schema from C# models
- **Migrations**: Automatic database updates
- **SQLite**: Lightweight, embedded database
- **Connection Pooling**: Optimized database connections

### SignalR Features
- **Real-time Updates**: Instant chat and status updates
- **Connection Management**: Automatic reconnection handling
- **Message Size Limits**: 1MB maximum message size
- **Error Handling**: Detailed errors in development mode

### Background Services
- **Isolated Processing**: Each account in separate background service
- **Graceful Shutdown**: Proper cleanup on application stop
- **Error Recovery**: Automatic reconnection and error handling
- **Resource Management**: Efficient memory and connection usage

### LibreMetaverse Integration
- **Full SL Protocol**: Complete Second Life protocol support
- **Asset Handling**: Avatar assets and textures
- **Group Support**: Group chat and notices
- **Teleportation**: Region and landmark teleporting
- **Inventory Management**: Basic inventory operations

### Plugin Architecture
- **AI Chat Bot**: Configurable AI-powered chat responses with multiple provider support
- **Corrade System**: Remote command execution via whisper commands
- **Modular Design**: Plugins can be enabled/disabled independently
- **Configuration Management**: Web-based configuration interfaces
- **Security Framework**: Group-based permissions and authentication

## 🌐 Browser Support

- Chrome 90+
- Firefox 88+
- Safari 14+
- Edge 90+

## 📝 Logging

### Application Logs
Application logs are written to:
- **Console**: Real-time development output
- **File System**: `./logs/radegast-web-{date}.log` (daily rotation)
- **Structured Format**: JSON-structured logs with Serilog

### Chat Logs
Each account maintains separate chat logs:
- **Location**: `./data/accounts/{accountId}/logs/`
- **Database**: Persistent chat history in SQLite
- **Real-time**: Live chat updates via SignalR

### Log Levels
- **Information**: Normal application flow
- **Warning**: Potential issues or unusual conditions
- **Error**: Error conditions that don't stop the application
- **Critical**: Serious errors that may cause the application to terminate

### Log Configuration
Configure logging levels in `appsettings.json`:
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.EntityFrameworkCore": "Warning",
        "Microsoft.AspNetCore": "Warning"
      }
    }
  }
}
```

## 🤝 Contributing

This project is based on the excellent [Radegast](https://github.com/cinderblocks/radegast) Second Life client. Key differences:

### Major Changes from Radegast
- **Removed WinForms GUI**: Complete removal of desktop UI components
- **Added ASP.NET Core**: Modern web framework with dependency injection
- **SignalR Integration**: Real-time web communication replacing desktop events
- **Multi-account Support**: Concurrent account management with isolation
- **Database Integration**: Entity Framework Core with SQLite for persistence
- **RESTful API**: Complete REST API for all operations
- **Modern Web UI**: Bootstrap 5 responsive interface with dark mode
- **Background Services**: Isolated background processing per account
- **Enhanced Logging**: Structured logging with Serilog

### Development Contributions
When contributing:
1. **Follow Architecture**: Use dependency injection and service patterns
2. **Database First**: Use Entity Framework migrations for schema changes
3. **API Design**: RESTful endpoints with proper HTTP status codes
4. **Real-time Updates**: Use SignalR for live updates
5. **Documentation**: Update Swagger documentation for API changes
6. **Testing**: Include unit tests for business logic
7. **Logging**: Use structured logging for debugging

### Code Standards
- **C# 12**: Use latest C# features and patterns
- **Async/Await**: Proper async patterns for I/O operations
- **SOLID Principles**: Clean architecture and separation of concerns
- **Error Handling**: Comprehensive error handling and logging

## 🙏 Acknowledgments

- **Radegast Team**: For the original Second Life client
- **LibreMetaverse**: For the Second Life protocol implementation
- **Second Life**: For the virtual world platform

## 📞 Support & Troubleshooting

### Common Issues

#### Connection Problems
- **Check Credentials**: Ensure correct SL username and password
- **Grid Status**: Verify Second Life grid is online
- **Network**: Check internet connection and firewall settings
- **Logs**: Review application logs for detailed error messages

#### Database Issues
- **Reset Database**: Delete `./data/radegast.db` and restart application
- **Migration Errors**: Run `dotnet ef database update`
- **Permissions**: Ensure write access to `./data/` directory

#### Web Interface Issues
- **JavaScript**: Ensure JavaScript is enabled in browser
- **Cache**: Clear browser cache and refresh
- **Console**: Check browser developer console for errors
- **SignalR**: Verify WebSocket support in browser

#### Performance Issues
- **Multiple Accounts**: Each account uses additional resources
- **Chat History**: Large chat history may slow interface
- **Region Load**: High-traffic regions may cause delays
- **AI Bot Usage**: AI responses consume API credits and may have rate limits

#### Plugin Issues
- **AI Bot Not Responding**: Check configuration file, API keys, and account login status
- **Corrade Commands Failing**: Verify group membership, passwords, and permissions
- **Plugin Configuration**: Use web interfaces for easier configuration management
- **API Rate Limits**: Monitor AI provider usage and adjust response frequencies

### Getting Help

For issues related to:
- **Second Life connectivity**: Check account credentials and grid status
- **Web interface**: Ensure modern browser with JavaScript enabled
- **Multiple accounts**: Each account needs unique, valid credentials
- **API usage**: Refer to Swagger documentation at `/swagger`

### Development Support
- **Logs**: Enable detailed logging in `appsettings.Development.json`
- **Debugging**: Use VS Code debugger with breakpoints
- **Hot Reload**: Use `dotnet watch run` for development
- **Database**: Use SQLite browser tools to inspect database

## 🔗 Related Projects

- [Radegast](https://github.com/cinderblocks/radegast) - Original desktop client
- [LibreMetaverse](https://github.com/cinderblocks/libremetaverse) - Second Life protocol library
- [OpenSimulator](http://opensimulator.org/) - Open source virtual world server

---

*RadegastWeb v1.2ß - Bringing Second Life to the modern web with AI and automation* 🌐✨🤖

### Version History
- **v1.2ß**: Current version with AI Chat Bot plugin, Corrade plugin, enhanced authentication, and web management interfaces
- **v1.1ß**: Database integration, display names, notices, and region information
- **v1.0**: Initial release with basic multi-account support and web interface

### License
This project maintains compatibility with the original Radegast licensing terms.