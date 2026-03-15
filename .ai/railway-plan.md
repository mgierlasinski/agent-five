# Railway Task Implementation Plan

## Objective

Implement the `railway` task in `src/AgentFive` so the system can activate route `X-01` through the undocumented hub API at `POST https://hub.ag3nts.org/verify`, starting with the `help` action, following the returned API instructions exactly, handling simulated overloads (`503`), respecting rate limits, and stopping only when the response contains a flag in the format `{FLG:...}`.

## Confirmed Decisions

- Use an LLM-driven agent loop as the primary execution model.
- Use `openai/gpt-5-mini` as the only model.
- Create a dedicated reusable hub client instead of placing raw `HttpClient` logic directly in `RailwayTask`.
- Wait automatically when rate-limit headers require a reset delay.
- Retry `503` responses with exponential backoff and jitter.
- Persist a full local artifact trail for every request and response.
- Cache the first successful `help` response locally for the current run.
- Keep retry and timing parameters as hardcoded constants inside the railway implementation.
- Validate mainly against the real hub, not with a heavy mock-first strategy.
- Persist the final flag and the full execution transcript.

## Remaining Assumptions

- The `help` action returns enough information to derive the exact action sequence, required parameters, and completion condition without external documentation.
- Rate-limit headers are present on both success and failure responses often enough to drive the waiting policy.
- The current `HubSettings` values are the correct source of `HubUrl` and `HubApiKey`.
- The route identifier `X-01` is an input to the agent, but the exact activation sequence must still come from `help`.
- The hub may return business errors in `200` responses or non-`200` responses, so the implementation must inspect both status and body.

## High-Level Architecture

### 1. `RailwayTask` as the orchestration entry point

`RailwayTask` should coordinate the full run and stay thin. Its responsibilities should be:

- create the logger-backed execution context
- create the dedicated railway hub client
- create the OpenRouter service
- bootstrap the agent with the route target `X-01`
- persist final outputs and log a compact summary
- dispose network resources safely

### 2. `RailwayHubClient` as the protocol boundary

Create a dedicated client under `src/AgentFive/Tasks/Railway` responsible for all HTTP communication with the hub. It should:

- send `POST verify` requests with the standard payload shape
- attach the API key from `HubSettings`
- parse response status, body, and all relevant headers
- recognize `503` as retryable overload
- recognize rate-limit reset signals and return a normalized waiting instruction
- persist raw requests and responses to artifact files
- expose a small typed result model to the rest of the task

This client should be reusable by later hub tasks because the retry, pacing, logging, and raw verification behavior are generic.

### 3. Agent loop over explicit tool calls

Use the existing `OpenRouterService` pattern from earlier tasks, but keep the tool surface very small and deterministic. The model should not call the hub directly; it should operate through tools backed by the railway client.

Recommended tools:

- `get_cached_help` or `request_help`
- `execute_api_action`
- `get_execution_history`
- `finish_with_result`

The purpose of the tool layer is to let the model interpret the returned `help` document and error messages while the code keeps control over retries, pacing, serialization, transcript persistence, and stop conditions.

## Execution Flow

### Phase 1. Bootstrap and guardrails

1. Validate `HubSettings.HubUrl` and `HubSettings.HubApiKey`.
2. Create an artifacts directory for the railway run, for example `Artifacts/railway/<timestamp-or-run-id>/`.
3. Initialize a transcript object containing:
   - run id
   - task name
   - target route `X-01`
   - selected model `openai/gpt-5-mini`
   - all action attempts
   - waiting decisions
   - final outcome

### Phase 2. Obtain and cache `help`

1. Send the minimal `help` payload as the very first hub action.
2. If a `503` occurs, retry with exponential backoff and jitter.
3. If rate-limit headers require waiting, sleep until reset before retrying or continuing.
4. Persist the first successful `help` response body as a dedicated artifact.
5. Cache the parsed `help` content in memory for the rest of the run.

No additional action guesses should be made before `help` succeeds.

### Phase 3. Agent-guided protocol execution

1. Provide the cached `help` response and the target route `X-01` to the model.
2. Instruct the model to:
   - derive the exact next action only from `help` and subsequent hub responses
   - never invent action names or parameters
   - minimize unnecessary hub calls
   - stop immediately when a flag is detected
3. For each requested action:
   - validate that the tool arguments contain a concrete action name
   - serialize the payload in the standard hub format
   - execute through `RailwayHubClient`
   - append normalized results to transcript history
   - return the body, headers summary, and retry metadata back to the model

### Phase 4. Completion detection

After every successful or business-error response, inspect the response body for `{FLG:`. Completion should not depend only on status code. The run ends successfully when a flag is detected anywhere in the relevant response content.

### Phase 5. Finalization

Persist:

- the full transcript JSON
- the final flag in a small summary file
- the last successful response
- the cached `help` document

Log a concise final summary including total hub calls, total retries, total wait time, and final flag.

## Request and Response Strategy

### Request envelope

All hub calls should use the known structure:

```json
{
  "apikey": "<hub key>",
  "task": "railway",
  "answer": {
    "action": "<value-from-help>",
    "...": "..."
  }
}
```

The implementation must keep the payload flexible because the exact additional fields are unknown until `help` is returned.

### Response normalization

Normalize each response into a typed structure such as:

- HTTP status code
- raw body
- parsed JSON if valid
- rate-limit headers
- retry-after or reset timestamp if present
- overload flag
- extracted flag if present
- timestamp and elapsed duration

This normalized model should be what the agent tools return, not raw `HttpResponseMessage` objects.

## Retry and Rate-Limit Policy

## 1. `503` handling

Use hardcoded constants inside the railway implementation, for example:

- initial delay: small but non-zero
- exponential multiplier: `2x`
- random jitter: small bounded addition
- maximum retry attempts per single action: conservative but sufficient

The retry loop should only retry conditions that are genuinely transient, mainly `503`.

## 2. Rate-limit handling

If the hub returns reset headers or explicit pacing instructions:

- compute the wait duration accurately
- log the reason for the wait
- sleep before the next attempt
- add the wait event to the transcript

If multiple headers exist, prefer the most conservative interpretation.

## 3. Non-retryable failures

Do not blindly retry:

- invalid payload errors
- invalid action names
- invalid ordering errors
- schema or validation errors clearly caused by wrong parameters

Those should be fed back into the agent loop so the next action can be corrected deliberately.

## Agent Prompting Strategy

The system prompt should explicitly constrain the model:

- start from `help` and trust only the hub output
- never invent undocumented actions or undocumented fields
- reuse cached knowledge from the first `help` response
- prefer the minimum number of hub calls
- interpret error messages as protocol guidance
- stop as soon as `{FLG:...}` appears
- return a structured final summary only

The user prompt should provide:

- task name `railway`
- target route `X-01`
- the operational constraints about `503` and rate limits
- the instruction to begin from the cached `help` response

## Suggested File Layout

Recommended additions under `src/AgentFive/Tasks/Railway`:

- `RailwayTask.cs` as orchestrator
- `RailwayHubClient.cs` for hub communication and pacing policy
- `RailwayModels.cs` for typed payload/result/transcript models
- `Tools/RailwayToolHandler.cs` for tool execution
- `Tools/RailwayToolProvider.cs` for model tool definitions

If the types remain small, `RailwayModels.cs` can be split later only if needed.

## Logging and Artifact Plan

Persist all artifacts under a dedicated railway folder. Each hub call should have:

- sequential index
- request JSON
- response body
- headers snapshot
- status code
- retry count
- elapsed time

Persist additional files:

- `Artifacts/railway-help-response.json` or `Artifacts/railway-help-response.txt`
- `Artifacts/railway-transcript.json`
- `Artifacts/railway-final-result.json`
- `Artifacts/railway-flag.txt`

Logs should remain concise at `Information` level and reserve verbose payload details for artifact files.

## Implementation Steps

1. Update `Program.cs` so `railway` is constructed with `HubSettings`, `OpenRouterSettings`, and `ILogger`, matching the existing task pattern.
2. Implement typed railway models for request envelopes, normalized responses, transcript entries, and final result.
3. Implement `RailwayHubClient` with:
   - `help` request support
   - generic action execution
   - `503` retry loop
   - rate-limit waiting
   - artifact persistence
4. Implement railway tools and tool handler for the agent loop.
5. Implement `RailwayTask.RunAsync()` to:
   - fetch `help`
   - launch the model-guided protocol run
   - detect the final flag
   - persist outputs
6. Add a strict response schema for the final agent output so completion data is machine-readable.
7. Run the task cautiously against the real hub and inspect artifacts after each failure before re-running.

## Validation Approach

Given the decision to rely mainly on the real hub:

- begin with a single careful run using `openai/gpt-5-mini`
- verify that the very first call is `help`
- verify that no action is sent before `help` succeeds
- verify that `503` events trigger backoff rather than immediate failure
- verify that reset headers cause waiting
- verify that each hub interaction is persisted locally
- verify that a final flag is captured and written to artifacts

The first successful run should then be used as a baseline transcript for future refactoring or reliability improvements.

## Risks and Mitigations

### Risk: The model wastes calls by over-exploring

Mitigation: keep the tool surface minimal, inject the cached `help` response directly, and instruct the model to minimize actions.

### Risk: The hub uses unexpected header names for rate limiting

Mitigation: log all headers, normalize the known ones first, and default to the most conservative wait behavior when ambiguity exists.

### Risk: `help` is long or loosely structured

Mitigation: cache it once, expose it to the model in full, and rely on the model only for interpretation, not transport control.

### Risk: Final success is returned in a non-obvious response shape

Mitigation: scan the raw response body for `{FLG:` after every action regardless of JSON shape or status code.

## Definition of Done

The implementation is complete when:

- `railway` can be launched from the existing console app entry point
- the first hub request is `help`
- all later actions follow the hub-returned protocol rather than hardcoded guesses
- `503` responses are retried automatically with exponential backoff and jitter
- rate-limit headers are respected automatically
- every request and response is persisted as artifacts
- the final flag is detected, logged, and written to disk
- the run leaves a transcript detailed enough to debug failures without guessing