// The app shares TypeScript packages with the web workspace (design tokens, i18n core, API client). They live outside
// this folder, so Metro is told to watch them.
const { getDefaultConfig } = require("expo/metro-config");
const path = require("path");

const config = getDefaultConfig(__dirname);
// contracts/ holds files the server and the app share (the danger-word list).
config.watchFolders = [...(config.watchFolders ?? []), path.resolve(__dirname, "../web/packages"), path.resolve(__dirname, "../contracts")];
config.resolver.unstable_enablePackageExports = true;
// The shared packages' runtime deps (e.g. openapi-fetch) are declared in this app's package.json and installed into
// mobile/node_modules. When a shared source file under ../web/packages imports one, Metro walks up from THAT file and
// would never reach mobile/node_modules — on EAS the web workspace is not installed, so the dep is missing. Point Metro
// at this app's node_modules as a resolution fallback so those imports resolve no matter which workspace is installed.
config.resolver.nodeModulesPaths = [...(config.resolver.nodeModulesPaths ?? []), path.resolve(__dirname, "node_modules")];

module.exports = config;
