const fs = require("fs");
const path = require("path");
const directory = process.argv[process.argv.indexOf("--fixture-directory") + 1];
function mark(stage, value = process.pid) {
  const target = path.join(directory, stage);
  fs.writeFileSync(target + ".tmp", String(value));
  fs.renameSync(target + ".tmp", target);
}
const isChild = process.argv.includes("--child");
mark(isChild ? "child-script-entered" : "script-entered");
function gate(stage) {
  mark(stage);
  // An explicit cold-start fault, never a readiness retry or a product timeout.
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 60000);
}

// A safety ceiling, not a readiness condition or cancellation timer. Both must
// be killed well before this expires; the test retains the actual child handle.
setTimeout(() => process.exit(0), 60000);
if (isChild) {
  if (process.argv.includes("--gate-child-start")) gate("child-start-gated");
  mark("child-ready");
} else {
  mark("before-spawn");
  if (process.argv.includes("--gate-before-spawn")) gate("spawn-gated");
  const child = require("child_process").spawn(process.execPath,
    [__filename, "--fixture-directory", directory, "--child",
      ...(process.argv.includes("--gate-child-start") ? ["--gate-child-start"] : [])],
    { stdio: "ignore", windowsHide: true });
  mark("after-spawn", child.pid);
  require("readline").createInterface({ input: process.stdin }).on("line", line => {
    if (JSON.parse(line).method === "initialize") mark("initialize-received");
  });
  mark("ready");
}
