# Onboarding Bot

Discord bot for handling user onboarding with AI-generated stories.

## Features

- Automated onboarding process with private threads
- AI-generated stories using OpenAI GPT-4
- Role management (Outsider → Associate)
- Invite tracking and inviter information
- Story storage and retrieval
- Existing user detection

## Environment Variables

Set these environment variables in your deployment platform:

- `DISCORD_TOKEN`: Your Discord bot token
- `OPENAI_KEY`: Your OpenAI API key

## Configuration

Update `appsettings.json` with your Discord server IDs:

```json
{
  "Discord": {
    "StoryChannelId": 1405280966422958100,
    "CityGatesChannelId": 0,
    "OutsiderRoleId": 0,
    "AssociateRoleId": 0,
    "GuildId": 0
  }
}
```

## Deployment on Render

1. Connect your GitHub repository to Render
2. Create a new Web Service
3. Set the following:
   - **Build Command**: `dotnet build -c Release -o out`
   - **Start Command**: `dotnet out/Onboardingbot.dll`
   - **Environment**: Docker
4. Add environment variables:
   - `DISCORD_TOKEN`
   - `OPENAI_KEY`
5. Deploy!

## Usage

Users can join the family by typing `!join` in the City Gates channel.

## Bot Permissions

The bot needs the following permissions:
- Send Messages
- Create Private Threads
- Manage Roles
- Read Message History
- Mention Everyone (for story channel)
