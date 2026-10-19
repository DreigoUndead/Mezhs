async function readConversationStream(response, reportProgress) {
  const state = {
    conversationId: null,
    execution: {},
    pending: "",
    thinkingReported: false,
    respondingReported: false
  };

  const consume = text => {
    state.pending += text;
    while (true) {
      const newline = state.pending.indexOf("\n");
      if (newline < 0) break;
      const line = state.pending.slice(0, newline).replace(/\r$/, "");
      state.pending = state.pending.slice(newline + 1);
      inspectConversationStreamLine(line, state, reportProgress);
    }
  };

  if (response.body?.getReader) {
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        consume(decoder.decode(value, { stream: true }));
      }
    } catch (error) {
      if (!state.conversationId)
        throw error;
      reportProgress?.({
        state: "waiting",
        detail: "Stream interrupted after conversation identification; recovering response from conversation state."
      });
    }
    consume(decoder.decode());
  } else {
    consume(await response.text());
  }

  if (state.pending)
    inspectConversationStreamLine(state.pending.replace(/\r$/, ""), state, reportProgress);

  return {
    conversationId: state.conversationId,
    execution: state.execution
  };
}

function inspectConversationStreamLine(line, state, reportProgress) {
  if (!line.startsWith("data:")) return;
  const data = line.slice(5).trim();
  if (!data || data === "[DONE]") return;

  let value;
  try {
    value = JSON.parse(data);
  } catch {
    return;
  }

  if (!state.conversationId)
    state.conversationId = findConversationIdInValue(value);

  collectStreamExecutionMetadata(value, state.execution);

  if (value?.type === "message_marker" && value.marker === "cot_token") {
    state.execution.reasoningObserved = true;
    if (!state.thinkingReported) {
      state.thinkingReported = true;
      reportProgress?.({
        state: "thinking",
        detail: "ChatGPT reported reasoning activity."
      });
    }
  }

  if (value?.type === "message_marker" && value.marker === "final_channel_token") {
    if (!state.respondingReported) {
      state.respondingReported = true;
      reportProgress?.({
        state: "responding",
        detail: "ChatGPT is generating the visible response."
      });
    }
  }
}

function findConversationIdInValue(value) {
  if (!value || typeof value !== "object") return null;
  if (typeof value.conversation_id === "string" && value.conversation_id)
    return value.conversation_id;
  for (const nested of Object.values(value)) {
    const found = findConversationIdInValue(nested);
    if (found) return found;
  }
  return null;
}

function collectStreamExecutionMetadata(value, execution) {
  if (!value || typeof value !== "object") return;

  if (value?.content?.content_type === "reasoning_recap")
    execution.reasoningObserved = true;

  collectToolMetadata(value, execution);

  const metadata = value.metadata;
  if (metadata && typeof metadata === "object")
    collectMetadataFields(metadata, execution);

  if (value.type === "server_ste_metadata" && metadata) {
    const experience = String(metadata.requested_model_experience || "").trim();
    if (experience) execution.requestedModelExperience ??= experience;
    const ttfvt = finiteNumber(metadata.server_ttfvt_ms);
    if (ttfvt !== null) execution.serverTtfvtMs ??= ttfvt;
  }

  for (const nested of Object.values(value))
    if (nested && typeof nested === "object")
      collectStreamExecutionMetadata(nested, execution);
}

function collectToolMetadata(value, execution) {
  const role = String(value?.author?.role || "").trim().toLowerCase();
  const recipient = String(value?.recipient || "").trim();
  const authorName = String(value?.author?.name || "").trim();
  const metadata = value?.metadata || {};

  if (role === "assistant" && recipient && recipient.toLowerCase() !== "all")
    addExecutionTool(execution, recipient);
  if (role === "tool" && authorName)
    addExecutionTool(execution, authorName);

  const metadataTool = String(metadata.tool_name || "").trim();
  if (metadataTool && metadata.tool_invoked !== false)
    addExecutionTool(execution, metadataTool);
}

function addExecutionTool(execution, tool) {
  const value = String(tool || "").trim();
  if (!value) return;
  execution.tools ??= [];
  if (!execution.tools.includes(value))
    execution.tools.push(value);
}

function collectMetadataFields(metadata, execution) {
  const model = String(
    metadata.resolved_model_slug ||
    metadata.model_slug ||
    ""
  ).trim();
  const thinkingEffort = String(metadata.thinking_effort || "").trim();
  const reasoningStatus = String(metadata.reasoning_status || "").trim();
  const reasoningStart = finiteNumber(metadata.reasoning_start_time);
  const reasoningEnd = finiteNumber(metadata.reasoning_end_time);

  if (model) execution.model ??= model;
  if (thinkingEffort) execution.thinkingEffort ??= thinkingEffort;
  if (reasoningStatus) execution.reasoningStatus ??= reasoningStatus;
  if (reasoningStatus || reasoningStart !== null || reasoningEnd !== null)
    execution.reasoningObserved = true;
  if (reasoningStart !== null)
    execution.reasoningStart = execution.reasoningStart === undefined
      ? reasoningStart
      : Math.min(execution.reasoningStart, reasoningStart);
  if (reasoningEnd !== null)
    execution.reasoningEnd = execution.reasoningEnd === undefined
      ? reasoningEnd
      : Math.max(execution.reasoningEnd, reasoningEnd);
}

function mergeExecutionMetadata(target, source, authoritative = false) {
  if (!source) return target;
  if (source.model) {
    if (authoritative) {
      target.model = source.model;
      target.thinkingEffort = source.thinkingEffort || null;
    } else {
      target.model ??= source.model;
    }
  }
  if (!authoritative && source.thinkingEffort)
    target.thinkingEffort ??= source.thinkingEffort;
  if (source.reasoningStatus) target.reasoningStatus ??= source.reasoningStatus;
  if (source.requestedModelExperience)
    target.requestedModelExperience ??= source.requestedModelExperience;
  if (source.serverTtfvtMs !== undefined)
    target.serverTtfvtMs ??= source.serverTtfvtMs;
  if (source.reasoningObserved)
    target.reasoningObserved = true;
  for (const tool of source.tools || [])
    addExecutionTool(target, tool);

  const start = finiteNumber(source.reasoningStart);
  const end = finiteNumber(source.reasoningEnd);
  if (start !== null)
    target.reasoningStart = target.reasoningStart === undefined
      ? start
      : Math.min(target.reasoningStart, start);
  if (end !== null)
    target.reasoningEnd = target.reasoningEnd === undefined
      ? end
      : Math.max(target.reasoningEnd, end);
  return target;
}

function finiteNumber(value) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function completionDetail(execution) {
  const facts = [];
  if (execution?.model) facts.push(`served model ${execution.model}`);
  if (execution?.requestedModel &&
      execution?.model &&
      execution.requestedModel !== execution.model)
    facts.push(`requested model ${execution.requestedModel}`);
  if (execution?.thinkingEffort) {
    facts.push(`effort ${execution.thinkingEffort}`);
  } else if (execution?.requestedThinkingEffort) {
    facts.push(`requested effort ${execution.requestedThinkingEffort}, not confirmed by provider`);
  }

  const start = finiteNumber(execution?.reasoningStart);
  const end = finiteNumber(execution?.reasoningEnd);
  if (start !== null && end !== null && end >= start) {
    facts.push(`reasoning ${(end - start).toFixed(1)}s`);
  } else if (execution?.reasoningObserved) {
    facts.push("reasoning observed");
  }
  if (execution?.tools?.length)
    facts.push(`tools ${execution.tools.join(", ")}`);

  return facts.length
    ? `Model response received (${facts.join(", ")}).`
    : "Model response received.";
}

module.exports = { readConversationStream, mergeExecutionMetadata, completionDetail, collectToolMetadata, collectMetadataFields, finiteNumber };
