import { afterEach, expect, test, vi } from "vitest";
import { getAttendance, submitAttendance } from "./attendanceApi";

const openShift = {
  id: 10,
  employeeId: 1,
  clockInAtUtc: "2028-02-29T23:53:35.1234567+00:00",
  clockOutAtUtc: null,
  status: "Open",
};
const attendance = { openShift, missingClockOutShifts: [] };

function respondWith(value: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockResolvedValueOnce(Response.json(value)),
  );
}

afterEach(() => vi.unstubAllGlobals());

test("accepts stored UTC timestamps with seven fractional digits and permits leap day", async () => {
  respondWith(attendance);
  await expect(getAttendance(1)).resolves.toEqual(attendance);
});

test.each([
  "0000-01-01T12:00:00Z",
  "2026-02-29T12:00:00Z",
  "2026-11-30T24:00:00Z",
  "2026-11-29T12:00:00",
  "2026-11-29",
])(
  "rejects a timestamp outside the API contract (%s)",
  async (clockInAtUtc) => {
    respondWith({ ...attendance, openShift: { ...openShift, clockInAtUtc } });
    await expect(getAttendance(1)).rejects.toThrow("invalid response");
  },
);

test.each([
  ["an absent openShift field", { missingClockOutShifts: [] }],
  [
    "a non-array missing shift list",
    { ...attendance, missingClockOutShifts: null },
  ],
  [
    "an open shift in the missing list",
    { ...attendance, missingClockOutShifts: [openShift] },
  ],
  [
    "a clock-out on an open shift",
    {
      ...attendance,
      openShift: { ...openShift, clockOutAtUtc: openShift.clockInAtUtc },
    },
  ],
])("rejects %s", async (_description, body) => {
  respondWith(body);
  await expect(getAttendance(1)).rejects.toThrow("invalid response");
});

test.each([
  ["the wrong resulting status", openShift],
  ["a completed shift with no exit", { ...openShift, status: "Completed" }],
  [
    "a completed shift with an invalid exit",
    { ...openShift, status: "Completed", clockOutAtUtc: "yesterday" },
  ],
  [
    "an exit 100 nanoseconds before entry",
    {
      ...openShift,
      status: "Completed",
      clockOutAtUtc: "2028-02-29T23:53:35.1234566Z",
    },
  ],
  [
    "an exit equal to entry with different zero padding and UTC suffix",
    {
      ...openShift,
      status: "Completed",
      clockInAtUtc: "2028-02-29T23:53:35.1+00:00",
      clockOutAtUtc: "2028-02-29T23:53:35.1000000Z",
    },
  ],
])("rejects a Clock Out response with %s", async (_description, body) => {
  respondWith(body);
  await expect(submitAttendance(1, "clock-out")).rejects.toThrow(
    "invalid response",
  );
});

test.each([
  ["2028-02-29T23:53:35.1234567+00:00", "2028-02-29T23:53:35.1234568Z"],
  ["2028-02-29T23:53:35Z", "2028-02-29T23:53:35.0000001+00:00"],
])(
  "accepts a Clock Out 100 nanoseconds after Clock In (%s → %s)",
  async (clockInAtUtc, clockOutAtUtc) => {
    const completed = {
      ...openShift,
      status: "Completed",
      clockInAtUtc,
      clockOutAtUtc,
    };
    respondWith(completed);
    await expect(submitAttendance(1, "clock-out")).resolves.toEqual(completed);
  },
);
