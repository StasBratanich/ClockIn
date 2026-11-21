// @vitest-environment jsdom
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
} from "@testing-library/react";
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import App from "./App";
import { requestTimeoutMs } from "./attendanceApi";
import type { Attendance, Shift } from "./attendanceApi";

const employees = [
  { id: 1, employeeNumber: "1001", fullName: "Alex Meyer" },
  { id: 2, employeeNumber: "1002", fullName: "Sam Keller" },
];

const empty: Attendance = { openShift: null, missingClockOutShifts: [] };
const openShift: Shift = {
  id: 10,
  employeeId: 1,
  clockInAtUtc: "2026-07-24T22:15:00Z",
  clockOutAtUtc: null,
  status: "Open",
};
const open: Attendance = { ...empty, openShift };
const attendancePath = "/api/employees/1/attendance";
const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200) {
  return Response.json(body, { status });
}

function button(name: string) {
  return screen.getByRole<HTMLButtonElement>("button", { name });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function selector() {
  return screen.getByRole<HTMLSelectElement>("combobox", { name: "Employee" });
}

function requests() {
  return fetchMock.mock.calls.map(([url, options]) => [
    options?.method ?? "GET",
    url,
  ]);
}

async function startEmployeeReload(initialResponse: Response) {
  const reloadResponse = deferred<Response>();
  fetchMock
    .mockResolvedValueOnce(initialResponse)
    .mockReturnValueOnce(reloadResponse.promise);
  await act(async () => render(<App />));

  expect(screen.getByRole("alert").textContent).toBe(
    "Could not load employees.",
  );
  expect(selector().disabled).toBe(true);
  const panel = screen.getByRole("region", { name: "Employee selection" });
  const reload = button("Reload employees");
  reload.focus();
  expect(document.activeElement).toBe(reload);

  const focusTargets: (EventTarget | null)[] = [];
  panel.addEventListener("focusin", (event) => focusTargets.push(event.target));
  await act(async () => fireEvent.click(reload));

  expect(screen.getByRole("status").textContent).toBe("Loading employees…");
  expect(screen.queryByRole("button", { name: "Reload employees" })).toBeNull();
  expect(document.activeElement).toBe(panel);
  expect(focusTargets).toEqual([panel]);
  return { reloadResponse, panel, focusTargets };
}

async function selectEmployee(state: Attendance) {
  fetchMock
    .mockResolvedValueOnce(json(employees))
    .mockResolvedValueOnce(json(state));
  render(<App />);
  await screen.findByRole("option", { name: "Alex Meyer (1001)" });
  fireEvent.change(selector(), { target: { value: "1" } });
  await screen.findByRole("heading", {
    name: state.openShift ? "Open shift" : "No open shift",
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

test("selects by internal employee ID and ignores a previous selection's late read", async () => {
  const late = deferred<Response>();
  fetchMock
    .mockResolvedValueOnce(json(employees))
    .mockReturnValueOnce(late.promise)
    .mockResolvedValueOnce(json(empty));
  render(<App />);

  expect(selector().disabled).toBe(true);
  expect(screen.getByText("Loading employees…")).toBeTruthy();
  await screen.findByRole("option", { name: "Sam Keller (1002)" });
  expect(requests()).toEqual([["GET", "/api/employees"]]);

  fireEvent.change(selector(), { target: { value: "1" } });
  expect(screen.getByText("Loading attendance…")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Clock In" })).toBeNull();
  fireEvent.change(selector(), { target: { value: "2" } });
  await screen.findByRole("heading", { name: "No open shift" });
  await act(async () => late.resolve(json(open)));

  expect(selector().value).toBe("2");
  expect(button("Clock In").disabled).toBe(false);
  expect(screen.queryByRole("heading", { name: "Open shift" })).toBeNull();
  expect(requests()).toEqual([
    ["GET", "/api/employees"],
    ["GET", attendancePath],
    ["GET", "/api/employees/2/attendance"],
  ]);
});

test("clocks in and out, shows the stored Zurich times, and refreshes after each action", async () => {
  await selectEmployee(empty);
  fetchMock
    .mockResolvedValueOnce(json(openShift))
    .mockResolvedValueOnce(json(open));
  fireEvent.click(button("Clock In"));
  await screen.findByRole("heading", { name: "Open shift" });

  expect(screen.getByText(/Clock In recorded/)).toBeTruthy();
  expect(screen.getAllByText("25 Jul 2026, 00:15")).toHaveLength(2);
  expect(screen.queryByRole("button", { name: "Clock In" })).toBeNull();

  const completed = {
    ...openShift,
    status: "Completed",
    clockOutAtUtc: "2026-07-25T07:30:00Z",
  };
  fetchMock
    .mockResolvedValueOnce(json(completed))
    .mockResolvedValueOnce(json(empty));
  fireEvent.click(button("Clock Out"));
  await screen.findByRole("heading", { name: "No open shift" });

  expect(screen.getByText(/Clock Out recorded/)).toBeTruthy();
  expect(screen.getByText("25 Jul 2026, 09:30").getAttribute("datetime")).toBe(
    completed.clockOutAtUtc,
  );
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/clock-in`],
    ["GET", attendancePath],
    ["POST", `${attendancePath}/clock-out`],
    ["GET", attendancePath],
  ]);
  expect(fetchMock.mock.calls[2][1]?.redirect).toBe("error");
  expect(fetchMock.mock.calls[2][1]?.body).toBeUndefined();
});

test("requires confirmation before marking a missing clock-out and shows only its stored start", async () => {
  await selectEmployee(open);
  fireEvent.click(button("I forgot to clock out"));
  expect(requests()).toHaveLength(2);
  button("Cancel").focus();
  fireEvent.click(button("Cancel"));
  expect(document.activeElement).toBe(button("I forgot to clock out"));
  expect(
    screen.queryByRole("button", { name: "Mark missing clock-out" }),
  ).toBeNull();
  expect(requests()).toHaveLength(2);

  const missing: Shift = { ...openShift, status: "MissingClockOut" };
  fetchMock
    .mockResolvedValueOnce(json(missing))
    .mockResolvedValueOnce(
      json({ ...empty, missingClockOutShifts: [missing] }),
    );
  fireEvent.click(button("I forgot to clock out"));
  button("Mark missing clock-out").focus();
  fireEvent.click(button("Mark missing clock-out"));
  const heading = screen.getByRole("heading", { name: "Attendance" });
  expect(document.activeElement).toBe(heading);
  await screen.findByRole("heading", { name: "No open shift" });
  expect(document.activeElement).toBe(heading);

  expect(screen.getByRole("listitem").textContent).toBe(
    "Clocked in 25 Jul 2026, 00:15",
  );
  expect(
    screen.getByText("Shift marked as missing its clock-out."),
  ).toBeTruthy();
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/missing-clock-out`],
    ["GET", attendancePath],
  ]);
});

test.each([
  { code: "TIMEOUT", failedAttemptReferenceId: 42 },
  { code: "PROVIDER_UNAVAILABLE" },
])(
  "shows $code and retries only after the user clicks Retry",
  async (problem) => {
    await selectEmployee(open);
    fetchMock.mockResolvedValueOnce(json(problem, 503));
    fireEvent.click(button("Clock Out"));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain(problem.code);
    if (problem.failedAttemptReferenceId) {
      expect(alert.textContent).toContain("Failed-attempt reference 42");
    } else {
      expect(alert.textContent).not.toContain("Failed-attempt reference");
    }
    expect(requests()).toHaveLength(3);
    expect(button("I forgot to clock out").disabled).toBe(false);

    fetchMock
      .mockResolvedValueOnce(
        json({
          ...openShift,
          status: "Completed",
          clockOutAtUtc: "2026-07-25T08:00:00Z",
        }),
      )
      .mockResolvedValueOnce(json(empty));
    fireEvent.click(button("Retry"));
    await screen.findByRole("heading", { name: "No open shift" });
    expect(requests().slice(2)).toEqual([
      ["POST", `${attendancePath}/clock-out`],
      ["POST", `${attendancePath}/clock-out`],
      ["GET", attendancePath],
    ]);
  },
);

test("disables duplicate submissions through a conflict and the refresh that follows", async () => {
  await selectEmployee(empty);
  const punch = deferred<Response>();
  const refresh = deferred<Response>();
  fetchMock
    .mockReturnValueOnce(punch.promise)
    .mockReturnValueOnce(refresh.promise);
  const clockIn = button("Clock In");
  fireEvent.click(clockIn);
  fireEvent.click(clockIn);
  expect(clockIn.disabled).toBe(true);
  expect(selector().disabled).toBe(true);
  expect(button("Refresh attendance").disabled).toBe(true);
  expect(screen.getByText("Submitting Clock In…")).toBeTruthy();
  expect(requests()).toHaveLength(3);

  await act(async () =>
    punch.resolve(json({ code: "OPEN_SHIFT_ALREADY_EXISTS" }, 409)),
  );
  expect(screen.getByRole("alert").textContent).toBe(
    "This employee already has an open shift.",
  );
  expect(screen.getByText("Loading attendance…")).toBeTruthy();
  expect(selector().disabled).toBe(true);
  expect(screen.queryByRole("button", { name: "Clock In" })).toBeNull();
  await act(async () => refresh.resolve(json(open)));
  expect(selector().disabled).toBe(false);
  expect(button("Clock Out").disabled).toBe(false);
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/clock-in`],
    ["GET", attendancePath],
  ]);
});

test.each(["network", "server", "invalid response"])(
  "refreshes after an uncertain %s failure without replaying the punch",
  async (failure) => {
    await selectEmployee(empty);
    if (failure === "network") {
      fetchMock.mockRejectedValueOnce(new TypeError("Network failure"));
    } else if (failure === "server") {
      fetchMock.mockResolvedValueOnce(json({ code: "INTERNAL_ERROR" }, 500));
    } else {
      fetchMock.mockResolvedValueOnce(
        json({ ...openShift, clockInAtUtc: "2026-02-30T10:00:00Z" }),
      );
    }
    fetchMock.mockResolvedValueOnce(json(open));
    fireEvent.click(button("Clock In"));
    await screen.findByRole("heading", { name: "Open shift" });

    expect(screen.getByRole("alert").textContent).toBe(
      "Could not confirm whether the action was saved.",
    );
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
    expect(screen.queryByText(/Clock In recorded/)).toBeNull();
    expect(requests().slice(2)).toEqual([
      ["POST", `${attendancePath}/clock-in`],
      ["GET", attendancePath],
    ]);
  },
);

test.each([
  "2026-07-24T22:14:59.9999999Z",
  "2026-07-24T22:15:00.0000000+00:00",
])(
  "reconciles a Clock Out response with a non-later exit (%s)",
  async (clockOutAtUtc) => {
    await selectEmployee(open);
    fetchMock
      .mockResolvedValueOnce(
        json({ ...openShift, status: "Completed", clockOutAtUtc }),
      )
      .mockResolvedValueOnce(json(empty));
    fireEvent.click(button("Clock Out"));
    await screen.findByRole("heading", { name: "No open shift" });

    expect(screen.getByRole("alert").textContent).toBe(
      "Could not confirm whether the action was saved.",
    );
    expect(screen.queryByText(/Clock Out recorded/)).toBeNull();
    expect(requests().slice(2)).toEqual([
      ["POST", `${attendancePath}/clock-out`],
      ["GET", attendancePath],
    ]);
  },
);

test("keeps actions unavailable when reconciliation fails and lets the user refresh the read", async () => {
  await selectEmployee(empty);
  fetchMock
    .mockResolvedValueOnce(json(openShift))
    .mockResolvedValueOnce(json({}, 500));
  fireEvent.click(button("Clock In"));
  await screen.findByRole("alert");
  expect(screen.getByText(/Clock In recorded/)).toBeTruthy();
  expect(screen.queryByRole("button", { name: /Clock (In|Out)/ })).toBeNull();

  fetchMock.mockResolvedValueOnce(json(open));
  fireEvent.click(button("Refresh attendance"));
  await screen.findByRole("heading", { name: "Open shift" });
  expect(button("Clock Out").disabled).toBe(false);
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/clock-in`],
    ["GET", attendancePath],
    ["GET", attendancePath],
  ]);
});

test("warns only after 12 elapsed hours while keeping punch controls available", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout"] });
  vi.setSystemTime(new Date("2026-07-25T10:14:59Z"));
  fetchMock
    .mockResolvedValueOnce(json(employees))
    .mockResolvedValueOnce(json(open));
  await act(async () => render(<App />));
  await act(async () =>
    fireEvent.change(selector(), { target: { value: "1" } }),
  );

  expect(screen.queryByText(/more than 12 hours/)).toBeNull();
  act(() => vi.advanceTimersByTime(1_000));
  expect(screen.queryByText(/more than 12 hours/)).toBeNull();
  act(() => vi.advanceTimersByTime(1));
  expect(screen.getByText(/more than 12 hours/)).toBeTruthy();
  expect(button("Clock Out").disabled).toBe(false);
  expect(button("I forgot to clock out").disabled).toBe(false);
  expect(requests()).toHaveLength(2);
});

test.each([
  ["HTTP error", () => new Response("Service unavailable", { status: 503 })],
  ["invalid data", () => json([{ ...employees[0], id: "1" }])],
] as const)(
  "recovers from an employee list %s via a user-triggered reload",
  async (_failure, response) => {
    const { reloadResponse, panel, focusTargets } =
      await startEmployeeReload(response());
    await act(async () => reloadResponse.resolve(json(employees)));

    expect(
      screen.getByRole("option", { name: "Alex Meyer (1001)" }),
    ).toBeTruthy();
    const employeeSelector = selector();
    expect(employeeSelector.disabled).toBe(false);
    expect(focusTargets).toEqual([panel, employeeSelector]);
    expect(document.activeElement).toBe(employeeSelector);
  },
);

test("returns keyboard focus to Reload employees when a reload fails", async () => {
  const { reloadResponse, panel, focusTargets } = await startEmployeeReload(
    json({}, 500),
  );
  await act(async () => reloadResponse.resolve(json({}, 500)));

  expect(screen.getByRole("alert").textContent).toBe(
    "Could not load employees.",
  );
  const reload = button("Reload employees");
  expect(focusTargets).toEqual([panel, reload]);
  expect(document.activeElement).toBe(reload);
});

test("keeps Reload employees available and focused after a successful empty reload", async () => {
  const { reloadResponse, panel, focusTargets } = await startEmployeeReload(
    json({}, 500),
  );
  await act(async () => reloadResponse.resolve(json([])));

  expect(screen.getByText("No employees are available.")).toBeTruthy();
  expect(selector().disabled).toBe(true);
  const reload = button("Reload employees");
  expect(reload.disabled).toBe(false);
  expect(focusTargets).toEqual([panel, reload]);
  expect(document.activeElement).toBe(reload);
});

test("keeps punch controls unavailable after an invalid attendance response and allows refresh", async () => {
  fetchMock
    .mockResolvedValueOnce(json(employees))
    .mockResolvedValueOnce(
      json({ ...empty, openShift: { ...openShift, employeeId: 2 } }),
    )
    .mockResolvedValueOnce(json(open));
  render(<App />);
  await screen.findByRole("option", { name: "Alex Meyer (1001)" });
  fireEvent.change(selector(), { target: { value: "1" } });

  expect((await screen.findByRole("alert")).textContent).toMatch(
    /Could not load attendance/,
  );
  expect(screen.queryByRole("button", { name: /Clock (In|Out)/ })).toBeNull();
  fireEvent.click(button("Refresh attendance"));
  await screen.findByRole("heading", { name: "Open shift" });
  expect(button("Clock Out").disabled).toBe(false);
});

test("bounds a stalled punch body and reconciliation, sends one POST, and allows a manual refresh", async () => {
  await selectEmployee(empty);
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  const stalledPunch = json(openShift);
  const punchBody = deferred<unknown>();
  vi.spyOn(stalledPunch, "json").mockReturnValue(punchBody.promise);
  const lateRead = deferred<Response>();
  fetchMock
    .mockResolvedValueOnce(stalledPunch)
    .mockReturnValueOnce(lateRead.promise);
  await act(async () => fireEvent.click(button("Clock In")));
  expect(selector().disabled).toBe(true);
  expect(requests()).toHaveLength(3);

  await act(async () => vi.advanceTimersByTimeAsync(requestTimeoutMs));
  expect(fetchMock.mock.calls[2][1]?.signal?.aborted).toBe(true);
  expect(
    screen.getByText("Could not confirm whether the action was saved."),
  ).toBeTruthy();
  expect(screen.getByText("Loading attendance…")).toBeTruthy();
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/clock-in`],
    ["GET", attendancePath],
  ]);

  await act(async () => vi.advanceTimersByTimeAsync(requestTimeoutMs));
  expect(fetchMock.mock.calls[3][1]?.signal?.aborted).toBe(true);
  expect(screen.getByText(/Could not load attendance/)).toBeTruthy();
  expect(selector().disabled).toBe(false);
  expect(button("Refresh attendance").disabled).toBe(false);
  expect(screen.queryByRole("button", { name: /Clock (In|Out)/ })).toBeNull();

  fetchMock.mockResolvedValueOnce(json(open));
  await act(async () => fireEvent.click(button("Refresh attendance")));
  await act(async () => lateRead.resolve(json(empty)));
  await act(async () => punchBody.resolve(openShift));
  expect(button("Clock Out").disabled).toBe(false);
  expect(screen.queryByText(/Clock In recorded/)).toBeNull();
  expect(requests().slice(2)).toEqual([
    ["POST", `${attendancePath}/clock-in`],
    ["GET", attendancePath],
    ["GET", attendancePath],
  ]);
});
