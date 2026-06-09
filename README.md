# Lugiano Medical Billing Automation

Workflow automation platform for Lugiano medical billing. The system detects key
clinical and billing workflow events in the **ChiroTouch** practice-management
database and drives cases through a defined workflow state machine, persisting all
events and state in a separate **WorkflowAutomation** database.

> **Current phase:** Prototype. The only goal right now is to prove **event
> detection** and **workflow state persistence**. AI scrubbing, the patient/staff
> portal, and email notifications are intentionally **out of scope** for this phase.

---

## Architecture

| Concern              | Choice                                                        |
| -------------------- | ------------------------------------------------------------- |
| Source database      | `PSChiro` / ChiroTouch (SQL Server) — **read-only**           |
| Target database      | `WorkflowAutomation` (SQL Server)                             |
| Prototype service    | .NET 8 Worker Service                                         |
| Source data access   | Dapper (read-only queries against the legacy ChiroTouch schema) |
| Workflow data access | EF Core 8 (code-first, migrations own the schema)             |
| Config               | Connection strings live in `appsettings.json`                |

### Hard rules

- **Never** write to or update any ChiroTouch source table. All ChiroTouch access
  is strictly read-only.
- The service must be **idempotent** and safe to restart at any time.
- Errors are caught and logged — a single bad record must never crash the worker.

---

## Repository structure

```
lugiano-workflow-automation/
│
├── backend/
│   ├── Lugiano.Workflow.SyncService/    # .NET 8 Worker Service (prototype lives here)
│   ├── Lugiano.Workflow.Domain/         # Entities, workflow states, event types
│   ├── Lugiano.Workflow.Infrastructure/ # DB access, connection factories, repositories
│   ├── Lugiano.Workflow.AI/             # (future) AI scrubbing — not implemented yet
│   └── Lugiano.Workflow.API/            # (future) backend API — not implemented yet
│
├── portal-api/                          # (future) NestJS API
├── portal-web/                          # (future) React frontend
│
├── database/
│   ├── workflow-db/                     # WorkflowAutomation schema definition
│   ├── scripts/                         # One-off / setup SQL
│   └── migrations/                      # Versioned schema migrations
│
├── docs/
│   ├── architecture/
│   ├── workflows/
│   └── diagrams/
│
├── docker/
└── README.md
```

### Prototype project layout (`backend/Lugiano.Workflow.SyncService`)

```
Lugiano.Workflow.SyncService/
├── Services/
│   ├── InsuranceSyncService.cs
│   ├── ChartNoteSyncService.cs
│   ├── WorkflowCaseService.cs
│   └── SyncStateService.cs
├── Models/
│   ├── SourceInsurancePolicy.cs
│   ├── SourcePatient.cs
│   ├── SourceChartNote.cs
│   ├── SourceChartText.cs
│   ├── WorkflowCase.cs
│   ├── WorkflowEvent.cs
│   ├── DoctorNote.cs
│   └── SyncState.cs
└── Data/
    ├── SourceDbConnectionFactory.cs
    └── WorkflowDbContext.cs   (or WorkflowDbConnectionFactory.cs)
```

---

## Workflow events

The prototype detects two events:

1. **Insurance Added / Verified** (`InsuranceAdded`)
2. **Doctor Notes Received** (`DoctorNoteReceived`)

### Workflow states

| State                   | Meaning                                            |
| ----------------------- | -------------------------------------------------- |
| `AwaitingInsurance`     | Case opened, no insurance yet                      |
| `AwaitingPipVerification` | Insurance added, pending PIP verification        |
| `AwaitingDoctorNotes`   | Waiting on clinical documentation                  |
| `ReadyForAiScrubbing`   | Doctor notes received, ready for the next phase    |

### Event types

- `InsuranceAdded`
- `DoctorNoteReceived`

---

## Source schema (ChiroTouch — read-only)

### `dbo.InsPolicies` (insurance)
`ID`, `PatientID`, `CoverageType`, `InsCoName`, `EffectiveDate`, `TerminationDate`, `Hidden`

### `dbo.Patients`
`ID`, `FirstName`, `LastName`

### `dbo.ChartNotes` (doctor notes)
`ID`, `PatientID`, `DoctorID`, `NoteDate`, `SOAPPtr`, `Status`

### `dbo.ChartText` (note text)
`Ptr`, `TextBody`, `NextPtr`

**Relationship:** `ChartNotes.SOAPPtr` → `ChartText.Ptr`. `ChartText.TextBody` holds the
clinical note in **RTF**. `ChartText.NextPtr` points to the next text chunk; follow the
chain until `NextPtr = 0` and concatenate all `TextBody` chunks.

---

## Target schema (WorkflowAutomation)

### `SyncState`
`Id`, `SyncKey`, `LastSeenId`, `UpdatedAt`

Keys: `LastSeenInsurancePolicyId`, `LastSeenChartNoteId`

### `WorkflowCase`
`Id`, `PatientId`, `FirstName`, `LastName`, `CurrentState`, `CreatedAt`, `UpdatedAt`

### `WorkflowEvent`
`Id`, `WorkflowCaseId`, `PatientId`, `EventType`, `SourceTable`, `SourceRecordId`, `EventDataJson`, `CreatedAt`

### `DoctorNote`
`Id`, `WorkflowCaseId`, `PatientId`, `ChartNoteId`, `DoctorId`, `NoteDate`, `SoapPtr`, `RawRtf`, `PlainText`, `CreatedAt`

---

## Polling behavior

The worker polls every **30 seconds** (prototype cadence).

### Insurance pass
1. Read `LastSeenInsurancePolicyId` from `SyncState`.
2. Query `dbo.InsPolicies` where `ID > LastSeenInsurancePolicyId` and `Hidden = 0`.
3. For each new record:
   - Join `Patients`.
   - Create or update the `WorkflowCase` for `PatientId`.
   - Set `CurrentState = AwaitingPipVerification`.
   - Insert a `WorkflowEvent` with `EventType = InsuranceAdded`.
   - Update `SyncState` after successful processing.

### Chart note pass
1. Read `LastSeenChartNoteId` from `SyncState`.
2. Query `dbo.ChartNotes` where `ID > LastSeenChartNoteId`.
3. For each new note:
   - Join `Patients`.
   - Create or update the `WorkflowCase` for `PatientId`.
   - Retrieve `ChartText` via `SOAPPtr`, following the `NextPtr` chain until `NextPtr = 0`.
   - Concatenate `TextBody` chunks; store as `RawRtf` on `DoctorNote`.
   - Convert RTF to plain text if possible, otherwise store `null` / a plain fallback.
   - Set `WorkflowCase.CurrentState = ReadyForAiScrubbing`.
   - Insert a `WorkflowEvent` with `EventType = DoctorNoteReceived`.
   - Update `SyncState` after successful processing.

---

## Idempotency & safeguards

- Never duplicate a `WorkflowEvent` for the same `SourceTable` + `SourceRecordId`.
- Never duplicate a `DoctorNote` for the same `ChartNoteId`.
- Log every processing step.
- Catch and log errors per record without crashing the worker.
- Never update ChiroTouch source tables.
- Keep all connection strings in `appsettings.json`.

---

## Prototype output

Console logs should report each cycle:

- Number of new insurance records found
- Number of new chart notes found
- Workflow cases created / updated
- Latest sync state values

---

## Running the worker

The WorkflowAutomation schema is **code-first**; EF Core migrations own it (no
hand-written DDL). On startup the worker applies any pending migrations, so the DB
is created/upgraded automatically.

```
# 1. Set both connection strings in
#    backend/Lugiano.Workflow.SyncService/appsettings.json
#      - ChiroTouch (read-only source)
#      - WorkflowAutomation (target; the login needs create/alter on first run)

# 2. Run the worker (applies migrations, then begins polling every 30s)
dotnet run --project backend/Lugiano.Workflow.SyncService
```

To manage the schema by hand instead of auto-migrating:

```
dotnet ef database update   --project backend/Lugiano.Workflow.SyncService
dotnet ef migrations add <Name> --project backend/Lugiano.Workflow.SyncService
```

---

## Out of scope (this phase)

- AI scrubbing (`Lugiano.Workflow.AI`)
- Patient/staff portal (`portal-api`, `portal-web`)
- Email notifications

These directories are reserved in the structure but intentionally unimplemented.
