Three conversations submit prompts at approximately the same moment.

{@body const [choice,setChoice] = DIL.useState("A")}
<radio-group direction="col" value={choice} onChange={setChoice}>
  <radio value="A">
    **A. Minimum interval between request starts**
    <text size="sm" color="secondary">Start Chat A at 0s, B at 5s, C at 10s. All three may then execute simultaneously. Recommended.</text>
  </radio>
  <radio value="B">
    **B. Cooldown after each completed request**
    <text size="sm" color="secondary">After a request finishes, require a 5-second cooldown before another request starts. Much more restrictive.</text>
  </radio>
  <radio value="C">
    **C. Delay only after rate limiting**
    <text size="sm" color="secondary">Normally start all three simultaneously. Slow down only following a rate-limit response or retry signal.</text>
  </radio>
  <radio value="D">
    **D. Different semantics**
    <text size="sm" color="secondary">Neither option describes the intended behavior.</text>
  </radio>
</radio-group>

<button block onClick={()=>GenUI.issueNewTurn(choice==="A"?"Grill Me Q1: A — minimum interval between request starts.":choice==="B"?"Grill Me Q1: B — cooldown after each completed request.":choice==="C"?"Grill Me Q1: C — delay only after rate limiting.":"Grill Me Q1: D — I want different semantics.")}>Confirm answer and continue <icon name="arrow-right" inline/></button>
