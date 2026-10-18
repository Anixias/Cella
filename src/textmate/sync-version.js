const fs = require("fs");
const path = require("path");

const version = fs.readFileSync(path.join(__dirname, "..", "..", "version.txt"), "utf8").trim();
const manifestPath = path.join(__dirname, "package.json");
const manifest = fs.readFileSync(manifestPath, "utf8");
const updated = manifest.replace(/("version":\s*")[^"]*"/, `$1${version}"`);
if (updated !== manifest)
	fs.writeFileSync(manifestPath, updated);
