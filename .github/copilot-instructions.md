# Copilot Instructions

## General Guidelines
- Always use string constants defined in `static classes` in `MusicSalesApp.Common\Helpers\` instead of inline magic strings for values that are written in one place and read/compared in another (event types, status names, setting keys, etc.). Both writer and reader must reference the same constant to avoid silent mismatches.

## Code Style
- Use specific formatting rules
- Follow naming conventions

## Project-Specific Rules
- Custom requirement A
- Custom requirement B