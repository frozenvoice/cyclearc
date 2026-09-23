const fs = require("fs");
const path = require("path");
const directory = process.argv[process.argv.indexOf("--fixture-directory") + 1];
function mark(stage) {
  const target = path.join(directory, stage);
  fs.writeFileSync(target + ".tmp", String(process.pid));
  fs.renameSync(target + ".tmp", target);
}

// A safety ceiling, not a readiness condition or cancellation timer. Both must
// be killed well before this expires; the test retains the actual child handle.
setTimeout(() => process.exit(0), 60000);
if (process.argv.includes("--child")) {
  mark("child-ready");
} else {
  require("child_process").spawn(process.execPath,
    [__filename, "--fixture-directory", directory, "--child"],
    { stdio: "ignore", windowsHide: true });
  require("readline").createInterface({ input: process.stdin }).on("line", line => {
    if (JSON.parse(line).method === "initialize") mark("initialize-received");
  });
  mark("ready");
}
