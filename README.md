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
- Visual Studio Code (recommended)

### Running the Application

1. **Clone and Navigate**
   ```powershell
   cd RadegastWeb
   ```

2. **Restore Dependencies**
   ```powershell
   dotnet restore
   ```

3. **Build the Project**
   ```powershell
   dotnet build
   ```

4. **Run the Application**
   ```powershell
   dotnet run
   ```

5. **Open in Browser**
   - Main application: `http://localhost:5269`
   - API documentation: `http://localhost:5269/swagger`
   - HTTPS version: `https://localhost:7077`

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
│   ├── PresenceController.cs     # Presence/status management
│   └── RegionController.cs       # Region information API
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
│   ├── ChatHistoryService.cs    # Chat logging and history
│   ├── DisplayNameService.cs    # Display name resolution
│   ├── NoticeService.cs         # Group notice handling
│   ├── PresenceService.cs       # Presence management
│   ├── RadegastBackgroundService.cs # Background SL processing
│   └── RegionInfoService.cs     # Region information
├── wwwroot/             # Static web files
│   ├── css/
│   │   ├── main.css             # Main stylesheet
│   │   └── region-info.css      # Region info styling
│   ├── js/
│   │   ├── main.js              # Main application logic
│   │   ├── presence-client.js   # Presence management client
│   │   └── region-info.js       # Region info client
│   └── index.html               # Main web interface
├── data/                # Runtime data
│   ├── radegast.db              # SQLite database
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

## 🔌 API Endpoints

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

### Presence Management
- `POST /api/presence/browser-close` - Handle browser close (set all to away)
- `POST /api/presence/{accountId}/status` - Update presence status
- `GET /api/presence/{accountId}` - Get current presence

### Region Information
- `GET /api/region/{accountId}/stats` - Get detailed region statistics
- `GET /api/region/{accountId}/info` - Get region information
- `POST /api/region/{accountId}/teleport` - Teleport to location

### Display Names
- `GET /api/displaynames/{accountId}` - Get cached display names
- `POST /api/displaynames/{accountId}/resolve` - Resolve specific display name

### Group Notices
- `GET /api/notices/{accountId}` - Get group notices
- `GET /api/notices/{accountId}/{noticeId}` - Get specific notice details

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
dotnet build
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

*RadegastWeb v1.1ß - Bringing Second Life to the modern web* 🌐✨

### Version History
- **v1.1ß**: Current version with full database integration, display names, notices, and region information
- **v1.0**: Initial release with basic multi-account support and web interface

### License
This project maintains compatibility with the original Radegast licensing terms.