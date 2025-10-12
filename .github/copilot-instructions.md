# Radegast Web Client - Multi-Account Second Life Client

This project is a web-based Second Life client inspired by Radegast, designed to support multiple concurrent accounts with isolated threads, cache folders, and log files.

## Project Overview
- **Type**: C# ASP.NET Core Web Application
- **Framework**: .NET 8.0
- **Real-time Communication**: SignalR
- **Architecture**: Multi-tenant with isolated account management
- **Base**: Radegast Second Life client (GUI removed, web interface added)

## Key Features
- Multiple concurrent Second Life accounts
- Isolated threads per account
- Separate cache and log directories for each account
- Real-time web interface using SignalR
- RESTful API for account management
- WebRTC support for voice (future enhancement)

## Development Guidelines
- Use dependency injection for service management
- Implement proper async/await patterns for SL protocol handling
- Each account runs in its own isolated context
- Use structured logging with Serilog
- Follow clean architecture principles
- Implement proper error handling and circuit breakers

## Account Isolation
- Each account has its own:
  - Thread context
  - Cache directory: `./data/accounts/{accountId}/cache/`
  - Log files: `./data/accounts/{accountId}/logs/`
  - Session state
  - Asset storage

## Technology Stack
- ASP.NET Core 8.0
- SignalR for real-time communication
- Entity Framework Core for data persistence
- Serilog for structured logging
- AutoMapper for object mapping
- FluentValidation for input validation
- Background services for SL protocol handling