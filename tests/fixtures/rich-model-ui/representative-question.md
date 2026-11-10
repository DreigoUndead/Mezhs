Which OpenTelemetry deployment should be used?

{@body const [choice,setChoice] = DIL.useState("B")}
<radio-group value={choice} onChange={setChoice}>
  <radio value="A">One provider, two instances</radio>
  <radio value="B">Two configured exporters</radio>
</radio-group>
<button onClick={() => GenUI.issueNewTurn(`Choice: ${choice}`)}>Confirm</button>

Further considerations appear below.