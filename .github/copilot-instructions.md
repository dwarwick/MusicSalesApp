# Copilot Instructions

## General Guidelines
- Always use string constants defined in `static classes` in `MusicSalesApp.Common\Helpers\` instead of inline magic strings for values that are written in one place and read/compared in another (event types, status names, setting keys, etc.). Both writer and reader must reference the same constant to avoid silent mismatches.
- Never hardcode email addresses (e.g., `support@streamtunes.net` or `customerservice@streamtunes.net`) in code that sends emails programmatically. Always read the customer service email from `IConfiguration["EmailSettings:CustomerServiceEmail"]`. See AGENTS.md "Customer Service Email Address" section for details.
- The web host is smarterasp.net (IIS-based), not Azure App Service. They have unlimited disk space for website files.
- Do not duplicate code across files. Extract shared logic into reusable helpers or services to make maintenance easier and adhere to the DRY (Don't Repeat Yourself) principle.

## Code Style
- Use specific formatting rules
- Follow naming conventions

## Project-Specific Rules
- Custom requirement A
- Custom requirement B