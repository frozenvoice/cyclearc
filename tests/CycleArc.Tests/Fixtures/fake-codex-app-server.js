const readline = require("readline");

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

function write(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n", "utf8");
}

rl.on("line", (line) => {
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    return;
  }

  if (message.method === "initialize") {
    const respond = () => write({ id: message.id, result: { protocolVersion: "1" } });
    if (process.argv.includes("--flood-stderr")) {
      // Respond only after a payload larger than the OS pipe buffer has drained.
      process.stderr.write("가".repeat(128 * 1024), "utf8", respond);
    } else {
      respond();
    }
    return;
  }

  if (message.method === "initialized") {
    write({ method: "session/ready", params: {} });
    return;
  }

  if (message.method === "account/read") {
    write({ id: message.id, result: {
      account: { type: "chatgpt", email: "synthetic@example.invalid", planType: "plus" },
      requiresOpenaiAuth: true
    } });
    return;
  }

  if (message.method === "account/rateLimits/read") {
    const response = {
      id: message.id,
      result: {
        ordinaryUsageAllowed: true,
        rateLimits: {
          limitId: "codex",
          primary: { usedPercent: 42, windowDurationMins: 300, resetsAt: 1893456000 },
          secondary: { usedPercent: 31, windowDurationMins: 10080, resetsAt: 1894051200 },
          planType: "pro",
          rateLimitReachedType: null
        },
        rateLimitsByLimitId: {
          codex: {
            limitId: "codex",
            primary: { usedPercent: 42, windowDurationMins: 300, resetsAt: 1893456000 },
            secondary: { usedPercent: 31, windowDurationMins: 10080, resetsAt: 1894051200 },
            planType: "pro",
            rateLimitReachedType: null
          }
        },
        rateLimitResetCredits: { availableCount: 1, credits: null }
      }
    };
    if (process.argv.includes("--flood-stderr")) {
      process.stdout.write(JSON.stringify(response) + "\n", "utf8", () => process.exit(0));
    } else {
      write(response);
    }
  }
});
