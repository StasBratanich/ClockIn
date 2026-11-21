export interface Employee {
  id: number;
  employeeNumber: string;
  fullName: string;
}

export interface Shift {
  id: number;
  employeeId: number;
  clockInAtUtc: string;
  clockOutAtUtc: string | null;
  status: "Open" | "Completed" | "MissingClockOut";
}

export interface Attendance {
  openShift: Shift | null;
  missingClockOutShifts: Shift[];
}

export type AttendanceAction = "clock-in" | "clock-out" | "missing-clock-out";

export const requestTimeoutMs = 15_000;

const expectedStatus = {
  "clock-in": "Open",
  "clock-out": "Completed",
  "missing-clock-out": "MissingClockOut",
} as const satisfies Record<AttendanceAction, Shift["status"]>;

export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly failedAttemptReferenceId?: number;

  constructor(
    status: number,
    code?: string,
    failedAttemptReferenceId?: number,
  ) {
    super(`Attendance API returned HTTP ${status}.`);
    this.status = status;
    this.code = code;
    this.failedAttemptReferenceId = failedAttemptReferenceId;
  }
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isId(value: unknown): value is number {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value > 0 &&
    value <= 2_147_483_647
  );
}

function isTimestamp(value: unknown): value is string {
  // The API serializes stored UTC instants with up to seven fractional digits.
  if (
    typeof value !== "string" ||
    value.startsWith("0000-") ||
    !/^\d{4}-\d{2}-\d{2}T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(
      value,
    )
  ) {
    return false;
  }

  const date = new Date(value.slice(0, 10) + "T00:00:00Z");
  return (
    Number.isFinite(date.getTime()) &&
    date.toISOString().slice(0, 10) === value.slice(0, 10)
  );
}

function isEmployee(value: unknown): value is Employee {
  return (
    isObject(value) &&
    isId(value.id) &&
    typeof value.employeeNumber === "string" &&
    typeof value.fullName === "string"
  );
}

function utcSortKey(timestamp: string): string {
  // Fixed-width UTC fields preserve the ordering of all seven fractional digits.
  const fraction = timestamp.slice(20, timestamp.endsWith("Z") ? -1 : -6);
  return timestamp.slice(0, 19) + fraction.padEnd(7, "0");
}

function isShiftWithStatus(
  value: unknown,
  employeeId: number,
  status: Shift["status"],
): value is Shift {
  return (
    isObject(value) &&
    isId(value.id) &&
    value.employeeId === employeeId &&
    value.status === status &&
    isTimestamp(value.clockInAtUtc) &&
    (status === "Completed"
      ? isTimestamp(value.clockOutAtUtc) &&
        utcSortKey(value.clockOutAtUtc) > utcSortKey(value.clockInAtUtc)
      : value.clockOutAtUtc === null)
  );
}

async function fetchValidated<T>(
  path: string,
  validate: (value: unknown) => value is T,
  init: RequestInit,
): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { Accept: "application/json, application/problem+json" },
    cache: "no-store",
  });
  if (!response.ok) {
    const problem: unknown = await response.json().catch(() => null);
    const fields = isObject(problem) ? problem : {};
    const { code, failedAttemptReferenceId } = fields;
    throw new ApiError(
      response.status,
      typeof code === "string" ? code : undefined,
      isId(failedAttemptReferenceId) ? failedAttemptReferenceId : undefined,
    );
  }

  const data: unknown = await response.json();
  if (!validate(data)) {
    throw new Error("Attendance API returned an invalid response.");
  }
  return data;
}

async function request<T>(
  path: string,
  validate: (value: unknown) => value is T,
  init?: RequestInit,
): Promise<T> {
  const controller = new AbortController();
  const { signal } = controller;
  const callerSignal = init?.signal;
  callerSignal?.throwIfAborted();
  const cancel = () => controller.abort(callerSignal?.reason);
  callerSignal?.addEventListener("abort", cancel, { once: true });

  let onAbort!: () => void;
  const interrupted = new Promise<never>((_, reject) => {
    onAbort = () => reject(signal.reason);
    signal.addEventListener("abort", onAbort, { once: true });
  });
  const timer = setTimeout(
    () =>
      controller.abort(
        new DOMException("Attendance request timed out.", "TimeoutError"),
      ),
    requestTimeoutMs,
  );

  try {
    return await Promise.race([
      fetchValidated(path, validate, { ...init, signal }),
      interrupted,
    ]);
  } finally {
    clearTimeout(timer);
    signal.removeEventListener("abort", onAbort);
    callerSignal?.removeEventListener("abort", cancel);
  }
}

export function getEmployees(signal: AbortSignal) {
  return request(
    "/api/employees",
    (value): value is Employee[] =>
      Array.isArray(value) && value.every(isEmployee),
    { signal },
  );
}

export function getAttendance(employeeId: number, signal?: AbortSignal) {
  return request(
    `/api/employees/${employeeId}/attendance`,
    (value): value is Attendance =>
      isObject(value) &&
      (value.openShift === null ||
        isShiftWithStatus(value.openShift, employeeId, "Open")) &&
      Array.isArray(value.missingClockOutShifts) &&
      value.missingClockOutShifts.every((shift) =>
        isShiftWithStatus(shift, employeeId, "MissingClockOut"),
      ),
    { signal },
  );
}

export function submitAttendance(employeeId: number, action: AttendanceAction) {
  // A redirect must not cause fetch to resend a punch.
  return request(
    `/api/employees/${employeeId}/attendance/${action}`,
    (value): value is Shift =>
      isShiftWithStatus(value, employeeId, expectedStatus[action]),
    { method: "POST", redirect: "error" },
  );
}
