{@body const [answer,setAnswer] = DIL.useState("A")}
<box border radius="xl" padding={3} gap={3}>
  <radio-group direction="col" value={answer} onChange={setAnswer}>
    <radio value="A">
      **A — Send to the collector.** One pipeline, one endpoint.
    </radio>
    <radio value="B">
      **B — Separate exporters.** Independently configurable destinations.
    </radio>
  </radio-group>
  <button block onClick={() => GenUI.issueNewTurn(`Choice: ${answer}`)}>Confirm</button>
</box>
