# AI Chat Bot Configuration

This document explains how to configure the AI Chat Bot for RadegastWeb.

## Configuration File

The AI bot is configured through `data/aibot.json`. The bot will only work when:
1. `enabled` is set to `true`
2. `avatarName` is set to a valid avatar name (e.g., "FirstName LastName")
3. A valid API key is provided
4. A system prompt is configured

## Basic Setup

1. Find your avatar name from the RadegastWeb accounts page (e.g., "John Doe")
2. Set `avatarName` to your avatar name exactly as shown
3. Configure your AI provider settings in `apiConfig`
4. Set `enabled` to `true`
5. Restart RadegastWeb

## AI Provider Examples

### OpenAI GPT-4
```json
{
  "provider": "openai",
  "apiUrl": "https://api.openai.com/v1",
  "apiKey": "sk-your-openai-key-here",
  "model": "gpt-4o-mini"
}
```

### Anthropic Claude
```json
{
  "provider": "anthropic",
  "apiUrl": "https://api.anthropic.com",
  "apiKey": "sk-ant-your-anthropic-key-here",
  "model": "claude-3-haiku-20240307"
}
```

### OpenRouter (Multiple Models)
```json
{
  "provider": "openai",
  "apiUrl": "https://openrouter.ai/api/v1",
  "apiKey": "sk-or-your-openrouter-key-here",
  "model": "meta-llama/llama-3.2-3b-instruct:free"
}
```

### Local Ollama
```json
{
  "provider": "openai",
  "apiUrl": "http://localhost:11434/v1",
  "apiKey": "not-needed",
  "model": "llama3.2:3b"
}
```

## Free GPT Options for Testing

### Groq (Recommended for Testing)
**Free Tier**: Very generous daily limits, extremely fast inference
```json
{
  "provider": "openai",
  "apiUrl": "https://api.groq.com/openai/v1",
  "apiKey": "gsk_your-groq-key-here",
  "model": "llama-3.1-8b-instant"
}
```

### OpenAI Free Tier
**Free Credits**: $5 in free credits for new accounts (3 months)
```json
{
  "provider": "openai",
  "apiUrl": "https://api.openai.com/v1",
  "apiKey": "sk-your-openai-key-here",
  "model": "gpt-3.5-turbo"
}
```

### Google AI Studio (Gemini)
**Free Tier**: Generous rate limits, long context windows
```json
{
  "provider": "google",
  "apiUrl": "https://generativelanguage.googleapis.com/v1beta",
  "apiKey": "your-gemini-api-key-here",
  "model": "gemini-1.5-flash"
}
```

### Hugging Face Inference API
**Free Tier**: Rate-limited but functional for testing
```json
{
  "provider": "huggingface",
  "apiUrl": "https://api-inference.huggingface.co/models",
  "apiKey": "hf_your-huggingface-token-here",
  "model": "microsoft/DialoGPT-medium"
}
```

### DeepSeek (Chinese Provider)
**Free Tier**: Competitive free limits
```json
{
  "provider": "openai",
  "apiUrl": "https://api.deepseek.com/v1",
  "apiKey": "sk-your-deepseek-key-here",
  "model": "deepseek-chat"
}
```

### Together.ai
**Free Credits**: $25 in free credits for new accounts
```json
{
  "provider": "openai",
  "apiUrl": "https://api.together.xyz/v1",
  "apiKey": "your-together-api-key-here",
  "model": "meta-llama/Llama-3-8b-chat-hf"
}
```

**Note**: Free tiers have rate limits and may require account verification. For production use, consider upgrading to paid plans for better reliability and higher limits.

## Configuration Options

### System Prompt
The `systemPrompt` defines your AI's personality and behavior. Make it specific to how you want your avatar to act in Second Life.

### Response Configuration
- `responseProbability`: Chance of responding to any message (0.0 to 1.0)
- `respondToNameMentions`: Respond when your avatar name is mentioned
- `respondToQuestions`: Respond to messages ending with "?"
- `triggerKeywords`: List of words that trigger responses
- `ignoreUuids`: Avatar UUIDs to never respond to (recommended - more secure)
- `ignoreNames`: Avatar names to ignore (legacy support, less secure)
- `minResponseDelaySeconds`/`maxResponseDelaySeconds`: Random delay before responding

### Chat History
- `includeHistory`: Include recent chat in AI context
- `maxHistoryMessages`: Number of previous messages to include
- `maxHistoryAgeMinutes`: Only include messages newer than this
- `maxHistoryCharacters`: Maximum total characters in all history messages (prevents huge payloads)
- `maxMessageLength`: Maximum characters per individual message (long messages get truncated)
- `includeBotMessages`: Include the bot's own previous messages

**Size Control**: The bot enforces multiple limits to prevent large API requests:
1. Message count limit (`maxHistoryMessages`)
2. Total character limit (`maxHistoryCharacters`) 
3. Individual message length limit (`maxMessageLength`)
4. Time-based limit (`maxHistoryAgeMinutes`)

The bot will stop adding history messages once any limit is reached, ensuring API requests stay manageable.

**Recommended Settings**:
- For basic chat: `maxHistoryCharacters: 1000`, `maxMessageLength: 100`
- For detailed context: `maxHistoryCharacters: 2500`, `maxMessageLength: 200`
- For minimal usage: `maxHistoryCharacters: 500`, `maxMessageLength: 50`

## Security Features

### UUID-Based Ignore List
The bot supports two types of ignore lists:

- **`ignoreUuids`** (Recommended): Uses avatar UUIDs for blocking
  - ✅ **Secure**: UUIDs cannot be changed or spoofed
  - ✅ **Permanent**: Works even if avatar changes display name
  - ✅ **Reliable**: Immune to name-based social engineering

- **`ignoreNames`** (Legacy): Uses avatar names for blocking
  - ⚠️ **Less Secure**: Names can be changed or spoofed
  - ⚠️ **Temporary**: May stop working if avatar changes name
  - ℹ️ **Convenience**: Easier to configure for known problem users

**How to get UUIDs**: Check the RadegastWeb chat logs or use SL viewer tools to find avatar UUIDs.

## Behavior

The AI bot will:
- ✅ Respond to local chat only
- ✅ Include recent chat history for context
- ✅ Add natural delays before responding
- ✅ Respect ignore lists and trigger conditions
- ✅ UUID-based blocking (secure, permanent)
- ❌ Never respond to IMs (instant messages)
- ❌ Never respond to group chat
- ❌ Never respond to whispers
- ❌ Never respond to its own messages

## Troubleshooting

1. Check the RadegastWeb logs for AI bot errors
2. Verify your API key is correct and has sufficient credits
3. Ensure the avatar name matches exactly (case insensitive, but spelling must be exact)
4. Test with a simple trigger keyword like "hello"
5. Check that `enabled` is `true` and not a string
6. Make sure the avatar account is logged in and connected

### Finding Avatar UUIDs for Ignore Lists

To get an avatar's UUID for secure blocking:
1. Look in RadegastWeb chat logs - UUIDs are logged with each message
2. Use the browser developer tools to inspect network requests
3. Use Second Life viewer's "About" feature on the avatar
4. Check the ChatMessageDto.SenderId in debug logs

## Security

- Keep your API keys secure
- Use API keys with limited permissions when possible
- Monitor your API usage and costs
- Consider rate limiting for high-traffic environments
- **Use UUID-based ignore lists** for reliable blocking of problem users
- Regularly review and update ignore lists

## Configuration Example

Example configuration for "John Smith" avatar:

```json
{
  "enabled": true,
  "avatarName": "John Smith",
  "systemPrompt": "You are John Smith, a friendly Second Life resident...",
  "apiConfig": {
    "provider": "openai",
    "apiKey": "sk-your-api-key-here",
    "model": "gpt-4o-mini"
  },
  "responseConfig": {
    "ignoreUuids": [
      "12345678-1234-1234-1234-123456789abc",
      "87654321-4321-4321-4321-cba987654321"
    ],
    "ignoreNames": [
      "KnownTroll",
      "SpamBot"
    ]
  }
}
```

**Note**: Avatar name must match exactly as shown in the RadegastWeb accounts page (case insensitive).