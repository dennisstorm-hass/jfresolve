# Changelog

All notable changes to the Jfresolve plugin are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Pagination support for TMDB API to fetch more than 20 items
- Multi-version support (10.9 and 11.0 compatible builds)
- Anime library path configuration
- FFmpeg customization toggle
- Daily scheduled library population at 3 AM UTC

### Fixed
- JSON parsing to handle numeric values from TMDB API
- UnreleasedBufferDays configuration not respecting 0 value
- ItemsPerRequest setting not being respected during population
- Anime show duplication in library population

### Changed
- Refactored codebase with utility classes for code reuse
- Improved error handling in library population
- Enhanced logging for debugging

## [11.0.0] - 2024-11-04

### Added
- Support for Jellyfin 11.0+
- .NET 9.0 framework upgrade
- Route parameter extraction utilities
- JSON helper utilities for consistent parsing
- UrlBuilder utility for URL normalization
- FileNameUtility for consistent file naming

### Changed
- Migrated from .NET 8.0 to .NET 9.0
- Refactored code with consolidated utility classes
- Improved STRM file generation logic

## [10.9.0] - 2024-11-04

### Initial Release
- Support for Jellyfin 10.9.x
- TMDB search integration
- Library population from TMDB
- STRM file generation
- External streaming addon support
- Configuration management
- Scheduled daily population

---

## How to Read This File

- **Added**: New features
- **Changed**: Changes in existing functionality
- **Deprecated**: Soon-to-be removed features
- **Removed**: Now removed features
- **Fixed**: Bug fixes
- **Security**: Security fixes

## Versioning Scheme

### Version Format: `MAJOR.MINOR.PATCH`

- **MAJOR**: Breaking changes or major feature additions
- **MINOR**: New features, backwards compatible
- **PATCH**: Bug fixes, backwards compatible

### Version Branches

- **10.9.x**: Jellyfin 10.9.x compatible versions
- **11.0.x**: Jellyfin 11.0+ compatible versions

## Release Process

1. Update CHANGELOG.md with changes
2. Update version in Plugin.cs if needed
3. Build in Release mode
4. Create GitHub release with ZIP file
5. Tag with version number (e.g., v10.9.1, v11.0.0)

---

Last updated: November 2024
