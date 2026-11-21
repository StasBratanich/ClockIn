import { useEffect, useRef, useState } from "react";
import { getEmployees } from "./attendanceApi";
import type { Employee } from "./attendanceApi";
import EmployeeAttendance from "./EmployeeAttendance";
import "./App.css";

type EmployeeList =
  { kind: "loading" | "error" } | { kind: "ready"; employees: Employee[] };

function App() {
  const [list, setList] = useState<EmployeeList>({ kind: "loading" });
  const [listRequest, setListRequest] = useState(0);
  const [selectedId, setSelectedId] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const selectionPanel = useRef<HTMLElement>(null);
  const employeeSelector = useRef<HTMLSelectElement>(null);
  const reloadButton = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const controller = new AbortController();
    getEmployees(controller.signal).then(
      (employees) => {
        if (!controller.signal.aborted) {
          setList({ kind: "ready", employees });
        }
      },
      () => {
        if (!controller.signal.aborted) {
          setList({ kind: "error" });
        }
      },
    );
    return () => controller.abort();
  }, [listRequest]);

  useEffect(() => {
    if (document.activeElement !== selectionPanel.current) {
      return;
    }
    if (reloadButton.current) {
      reloadButton.current.focus();
    } else if (
      list.kind === "ready" &&
      employeeSelector.current?.disabled === false
    ) {
      employeeSelector.current.focus();
    }
  }, [list.kind]);

  const employees = list.kind === "ready" ? list.employees : [];
  const isEmpty = list.kind === "ready" && employees.length === 0;
  const selected = employees.find(
    (employee) => String(employee.id) === selectedId,
  );

  return (
    <main className="attendance">
      <header className="page-heading">
        <h1>ClockIn</h1>
        <p>Employee attendance</p>
      </header>

      <section
        className="attendance-panel"
        aria-label="Employee selection"
        ref={selectionPanel}
        tabIndex={-1}
      >
        <label htmlFor="employee">Employee</label>
        <select
          id="employee"
          ref={employeeSelector}
          value={selectedId}
          onChange={(event) => setSelectedId(event.target.value)}
          disabled={employees.length === 0 || submitting}
          aria-describedby="employee-help"
        >
          <option value="">Select an employee</option>
          {employees.map((employee) => (
            <option key={employee.id} value={employee.id}>
              {employee.fullName} ({employee.employeeNumber})
            </option>
          ))}
        </select>
        <p id="employee-help" className="muted">
          Selecting an employee does not verify their identity.
        </p>
        {list.kind === "loading" && <p role="status">Loading employees…</p>}
        {list.kind === "error" && <p role="alert">Could not load employees.</p>}
        {isEmpty && <p>No employees are available.</p>}
        {(list.kind === "error" || isEmpty) && (
          <button
            type="button"
            className="secondary"
            ref={reloadButton}
            onClick={() => {
              selectionPanel.current?.focus();
              setList({ kind: "loading" });
              setListRequest(listRequest + 1);
            }}
          >
            Reload employees
          </button>
        )}
      </section>

      {selected && (
        <EmployeeAttendance
          key={selected.id}
          employeeId={selected.id}
          setSubmitting={setSubmitting}
        />
      )}
    </main>
  );
}

export default App;
