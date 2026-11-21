import { expect, test } from "vitest";
import { formatZurichTime, millisecondsUntilLongShift } from "./attendanceTime";

test("formats Zurich dates and wall times across midnight and in both offsets", () => {
  expect(formatZurichTime("2026-07-24T22:15:00Z")).toBe("25 Jul 2026, 00:15");
  expect(formatZurichTime("2026-11-21T22:15:00Z")).toBe("21 Nov 2026, 23:15");
});

test("uses elapsed hours across a Zurich daylight saving transition", () => {
  const clockIn = "2026-03-28T20:00:00Z";
  expect(
    millisecondsUntilLongShift(clockIn, Date.parse("2026-03-29T07:59:59.999Z")),
  ).toBe(1);
  expect(
    millisecondsUntilLongShift(clockIn, Date.parse("2026-03-29T08:00:00Z")),
  ).toBe(0);
  expect(
    millisecondsUntilLongShift(clockIn, Date.parse("2026-03-29T08:00:00.001Z")),
  ).toBe(-1);
});
