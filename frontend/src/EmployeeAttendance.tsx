import { useEffect, useRef, useState } from "react";
import { ApiError, getAttendance, submitAttendance } from "./attendanceApi";
import type { Attendance, AttendanceAction, Shift } from "./attendanceApi";
import { formatZurichTime, millisecondsUntilLongShift } from "./attendanceTime";

const actionLabels = {
  "clock-in": "Clock In",
  "clock-out": "Clock Out",
  "missing-clock-out": "Mark missing clock-out",
};

const conflictMessages = new Map([
  ["OPEN_SHIFT_ALREADY_EXISTS", "This employee already has an open shift."],
  ["NO_OPEN_SHIFT", "This employee has no open shift."],
  [
    "OPEN_SHIFT_CHANGED",
    "The open shift changed while the action was in progress.",
  ],
  ["CLOCK_OUT_NOT_AFTER_CLOCK_IN", "Clock Out must be after Clock In."],
]);

type AttendanceRead =
  | { kind: "loading" }
  | { kind: "error"; message: string }
  | { kind: "ready"; attendance: Attendance };

type Feedback =
  | { kind: "success"; message: string; timestamp?: string }
  | { kind: "error"; message: string }
  | {
      kind: "provider";
      code: string;
      reference?: number;
      action: AttendanceAction;
    };

async function readAttendance(
  employeeId: number,
  signal?: AbortSignal,
): Promise<AttendanceRead> {
  try {
    const attendance = await getAttendance(employeeId, signal);
    return { kind: "ready", attendance };
  } catch (error) {
    const message =
      error instanceof ApiError && error.code === "EMPLOYEE_NOT_FOUND"
        ? "This employee is no longer available."
        : "Could not load attendance. Refresh attendance to try again.";
    return { kind: "error", message };
  }
}

function PunchTime({ timestamp }: { timestamp: string }) {
  return <time dateTime={timestamp}>{formatZurichTime(timestamp)}</time>;
}

function LongShiftWarning({ clockIn }: { clockIn: string }) {
  const [showWarning, setShowWarning] = useState(
    () => millisecondsUntilLongShift(clockIn, Date.now()) < 0,
  );

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;

    function scheduleCheck() {
      const remaining = millisecondsUntilLongShift(clockIn, Date.now());
      timer = setTimeout(
        () => {
          setShowWarning(millisecondsUntilLongShift(clockIn, Date.now()) < 0);
          scheduleCheck();
        },
        remaining >= 0 ? Math.min(remaining + 1, 60_000) : 60_000,
      );
    }

    scheduleCheck();
    return () => clearTimeout(timer);
  }, [clockIn]);

  return (
    showWarning && (
      <p className="notice warning" role="status">
        This shift has been open for more than 12 hours. Review it before
        continuing.
      </p>
    )
  );
}

function successFeedback(action: AttendanceAction, shift: Shift): Feedback {
  switch (action) {
    case "clock-in":
      return {
        kind: "success",
        message: "Clock In recorded.",
        timestamp: shift.clockInAtUtc,
      };
    case "clock-out":
      return {
        kind: "success",
        message: "Clock Out recorded.",
        timestamp: shift.clockOutAtUtc ?? undefined,
      };
    case "missing-clock-out":
      return {
        kind: "success",
        message: "Shift marked as missing its clock-out.",
      };
  }
}

function actionErrorFeedback(
  action: AttendanceAction,
  error: unknown,
): Feedback {
  if (!(error instanceof ApiError)) {
    return {
      kind: "error",
      message: "Could not confirm whether the action was saved.",
    };
  }

  const { status, code, failedAttemptReferenceId } = error;
  if (
    status === 503 &&
    action !== "missing-clock-out" &&
    (code === "TIMEOUT" ||
      code === "PROVIDER_UNAVAILABLE" ||
      code === "INVALID_RESPONSE")
  ) {
    return {
      kind: "provider",
      code,
      reference: failedAttemptReferenceId,
      action,
    };
  }

  if (status === 409) {
    return {
      kind: "error",
      message:
        conflictMessages.get(code ?? "") ??
        "The action conflicts with the current attendance state.",
    };
  }
  if (status === 404) {
    return { kind: "error", message: "The employee could not be found." };
  }
  if (status >= 500 || status === 408) {
    return {
      kind: "error",
      message: "Could not confirm whether the action was saved.",
    };
  }
  return { kind: "error", message: "The attendance action was rejected." };
}

interface Props {
  employeeId: number;
  setSubmitting: (submitting: boolean) => void;
}

export default function EmployeeAttendance({
  employeeId,
  setSubmitting,
}: Props) {
  const [read, setRead] = useState<AttendanceRead>({ kind: "loading" });
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const [actionPending, setActionPending] = useState<AttendanceAction | null>(
    null,
  );
  const [confirming, setConfirming] = useState(false);
  const submissionInProgress = useRef(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const missingClockOutDisclosure = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    void readAttendance(employeeId, controller.signal).then((result) => {
      if (!controller.signal.aborted) {
        setRead(result);
      }
    });
    return () => controller.abort();
  }, [employeeId]);

  const attendance = read.kind === "ready" ? read.attendance : null;
  const openShift = attendance?.openShift;

  async function act(action: AttendanceAction) {
    if (
      submissionInProgress.current ||
      !attendance ||
      Boolean(openShift) === (action === "clock-in")
    ) {
      return;
    }

    submissionInProgress.current = true;
    heading.current?.focus();
    setSubmitting(true);
    setActionPending(action);
    setConfirming(false);
    setFeedback(null);

    try {
      const result = await submitAttendance(employeeId, action).then(
        (shift) => successFeedback(action, shift),
        (error: unknown) => actionErrorFeedback(action, error),
      );
      setFeedback(result);
      if (result.kind !== "provider") {
        setRead({ kind: "loading" });
        setRead(await readAttendance(employeeId));
      }
    } finally {
      submissionInProgress.current = false;
      setActionPending(null);
      setSubmitting(false);
    }
  }

  async function refresh() {
    setConfirming(false);
    setFeedback((current) => (current?.kind === "provider" ? null : current));
    setRead({ kind: "loading" });
    setRead(await readAttendance(employeeId));
  }

  return (
    <section className="attendance-panel" aria-labelledby="attendance-heading">
      <header className="section-heading">
        <div>
          <h2 id="attendance-heading" ref={heading} tabIndex={-1}>
            Attendance
          </h2>
          <p className="muted">Times in Europe/Zurich</p>
        </div>
        <button
          type="button"
          className="secondary"
          onClick={() => void refresh()}
          disabled={read.kind === "loading" || actionPending !== null}
        >
          Refresh attendance
        </button>
      </header>

      {actionPending && read.kind !== "loading" && (
        <p role="status">Submitting {actionLabels[actionPending]}…</p>
      )}
      {feedback?.kind === "success" && (
        <p role="status" className="notice success">
          {feedback.message}{" "}
          {feedback.timestamp && <PunchTime timestamp={feedback.timestamp} />}
        </p>
      )}
      {feedback?.kind === "error" && (
        <p role="alert" className="notice error">
          {feedback.message}
        </p>
      )}
      {feedback?.kind === "provider" && (
        <div className="notice error">
          <p role="alert">
            The time provider could not supply a time for{" "}
            {actionLabels[feedback.action]}. Error code{" "}
            <strong>{feedback.code}</strong>.
            {feedback.reference !== undefined && (
              <>
                {" "}
                Failed-attempt reference <strong>{feedback.reference}</strong>.
              </>
            )}
          </p>
          <button
            type="button"
            className="secondary"
            disabled={confirming}
            onClick={() => void act(feedback.action)}
          >
            Retry
          </button>
        </div>
      )}
      {read.kind === "loading" && <p role="status">Loading attendance…</p>}
      {read.kind === "error" && (
        <p role="alert" className="notice error">
          {read.message}
        </p>
      )}
      {attendance && (
        <>
          <div className="shift-state">
            <h3>{openShift ? "Open shift" : "No open shift"}</h3>
            {openShift && (
              <>
                <p>
                  Clocked in <PunchTime timestamp={openShift.clockInAtUtc} />
                </p>
                <LongShiftWarning
                  key={openShift.id}
                  clockIn={openShift.clockInAtUtc}
                />
              </>
            )}
            <div className="actions">
              {openShift ? (
                <>
                  <button
                    type="button"
                    disabled={actionPending !== null || confirming}
                    onClick={() => void act("clock-out")}
                  >
                    Clock Out
                  </button>
                  <button
                    type="button"
                    className="secondary"
                    ref={missingClockOutDisclosure}
                    disabled={actionPending !== null}
                    aria-expanded={confirming}
                    onClick={() => setConfirming(!confirming)}
                  >
                    I forgot to clock out
                  </button>
                </>
              ) : (
                <button
                  type="button"
                  disabled={actionPending !== null}
                  onClick={() => void act("clock-in")}
                >
                  Clock In
                </button>
              )}
            </div>
            {confirming && (
              <div className="notice warning">
                <p>
                  Mark this shift as missing its clock-out? Its end time will
                  stay blank.
                </p>
                <div className="actions">
                  <button
                    type="button"
                    onClick={() => void act("missing-clock-out")}
                  >
                    Mark missing clock-out
                  </button>
                  <button
                    type="button"
                    className="secondary"
                    onClick={() => {
                      setConfirming(false);
                      missingClockOutDisclosure.current?.focus();
                    }}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            )}
          </div>

          <div className="missing-shifts">
            <h3>Missing clock-outs</h3>
            {attendance.missingClockOutShifts.length === 0 ? (
              <p className="muted">No shifts with a missing clock-out.</p>
            ) : (
              <ul>
                {attendance.missingClockOutShifts.map((shift) => (
                  <li key={shift.id}>
                    Clocked in <PunchTime timestamp={shift.clockInAtUtc} />
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}
    </section>
  );
}
