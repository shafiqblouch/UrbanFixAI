// The app shares TypeScript packages with the web workspace (design tokens, i18n core, API client). They live outside
// this folder, so Metro is told to watch them. Each of them resolves its own dependencies (openapi-fetch) from its own folder.
const { getDefaultConfig } = require("expo/metro-config");
const path = require("path");

const config = getDefaultConfig(__dirname);
// contracts/ holds files the server and the app share (the danger-word list).
config.watchFolders = [...(config.watchFolders ?? []), path.resolve(__dirname, "../web/packages"), path.resolve(__dirname, "../contracts")];
config.resolver.unstable_enablePackageExports = true;

module.exports = config;
