const zurichTime = new Intl.DateTimeFormat("en-GB", {
  timeZone: "Europe/Zurich",
  dateStyle: "medium",
  timeStyle: "short",
});

const warningThresholdMs = 12 * 60 * 60 * 1000;

export function formatZurichTime(timestamp: string) {
  return zurichTime.format(new Date(timestamp));
}

export function millisecondsUntilLongShift(clockIn: string, now: number) {
  return Date.parse(clockIn) + warningThresholdMs - now;
}
