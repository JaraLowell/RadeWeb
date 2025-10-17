# AI Chat Bot Configuration

This document explains how to configure the AI Chat Bot for RadegastWeb.

## Configuration File

The AI bot is configured through `data/aibot.json`. The bot will only work when:
1. `enabled` is set to `true`
2. `linkedAccountId` is set to a valid account ID (required for account filtering)
3. A valid API key is provided
4. A system prompt is configured
5. `avatarName` is set (optional - used only for AI context, not filtering)

## Basic Setup

1. Get the account ID from the RadegastWeb accounts page (required)
2. Set `linkedAccountId` to your account ID to link the bot to that specific account
3. Set `avatarName` to your avatar name (used by AI for context, not for filtering)
4. Configure your AI provider settings in `apiConfig`
5. Set `enabled` to `true`
6. Restart RadegastWeb

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

### Account Linking
- `linkedAccountId`: The specific account UUID that this AI bot is linked to
  - **Required**: Must be set for the bot to function
  - **Format**: Standard UUID format (e.g., "12345678-abcd-1234-5678-123456789abc")
  - **Where to find**: Check the RadegastWeb accounts page for your account ID
  - **Behavior**: Only chat received by this specific account will trigger AI responses
  - **Security**: Prevents the bot from responding on wrong accounts in multi-account setups

### Avatar Name
- `avatarName`: The avatar name for AI context (e.g., "FirstName LastName")
  - **Purpose**: Used by the AI to know what name to use in responses and roleplay
  - **Not used for filtering**: This field does NOT determine which account's chat to process
  - **Optional but recommended**: Helps the AI maintain consistent character identity

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
3. Ensure the `linkedAccountId` is set and matches an existing account ID
4. Ensure the avatar name is set (for AI context, not filtering)
5. Test with a simple trigger keyword like "hello"
6. Check that `enabled` is `true` and not a string
7. Make sure the linked avatar account is logged in and connected

### Account Filtering Priority
The AI bot filters incoming chat in this order:
1. **Account ID Check**: Only processes chat from the account specified in `linkedAccountId`
2. **Chat Type Check**: Only processes "normal" local chat (not IMs, groups, whispers)
3. **Self-Message Check**: Ignores messages from the bot's own avatar
4. **Ignore Lists**: Checks UUID and name-based ignore lists
5. **Trigger Conditions**: Evaluates response probability and trigger keywords

**Note**: `avatarName` is NOT used for filtering - it's only used by the AI for context and roleplay.

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

## Configuration Examples

### Basic Example - "John Smith" avatar:

```json
{
  "enabled": true,
  "linkedAccountId": "12345678-abcd-1234-5678-123456789abc",
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

### Roleplay Example - "Bunny" character using Groq (Free):

```json
{
  "enabled": true,
  "linkedAccountId": "87654321-dcba-4321-8765-fedcba987654",
  "avatarName": "Bunny",
  "systemPrompt": "You are Bunny, a cheerful and helpful avatar in Second Life. You have dark hair and striking blue eyes, and you're currently wearing a stylish Adidas swimsuit. Your personality is bubbly, optimistic, and always ready to lend a helping hand to other residents. You love meeting new people, exploring virtual worlds, and participating in beach activities or water sports. You speak in a friendly, upbeat manner and often use cute expressions. You're knowledgeable about Second Life activities like shopping, events, clubs, and social gatherings. When someone needs help with anything - from finding places to avatar customization - you're always eager to assist with enthusiasm. Keep your responses concise but warm, and remember you're in a virtual beach/water setting. Use emotes occasionally like *giggles*, *splashes playfully*, or *adjusts swimsuit* to show your playful nature.",
  "apiConfig": {
    "provider": "openai",
    "apiUrl": "https://api.groq.com/openai/v1",
    "apiKey": "gsk_your-groq-api-key-here",
    "model": "llama-3.1-8b-instant"
  },
  "responseConfig": {
    "responseProbability": 0.7,
    "respondToNameMentions": true,
    "respondToQuestions": true,
    "triggerKeywords": [
      "help",
      "bunny",
      "swim",
      "beach",
      "cute",
      "hi",
      "hello",
      "outfit",
      "hair",
      "eyes",
      "adidas"
    ],
    "ignoreUuids": [],
    "ignoreNames": [],
    "minResponseDelaySeconds": 2,
    "maxResponseDelaySeconds": 5
  },
  "historyConfig": {
    "includeHistory": true,
    "maxHistoryMessages": 8,
    "maxHistoryAgeMinutes": 15,
    "maxHistoryCharacters": 1500,
    "maxMessageLength": 150,
    "includeBotMessages": true
  }
}
```

**Note**: Avatar name must match exactly as shown in the RadegastWeb accounts page (case insensitive).