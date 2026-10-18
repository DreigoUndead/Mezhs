const { API, apiJson, parseRetryAfterMs } = require('./account-transport.js');
const { collectFileRefs } = require('./account-files.js');
const { modelSelectionId } = require('./account-models.js');
const { mergeExecutionMetadata, completionDetail, collectToolMetadata, collectMetadataFields, finiteNumber } = require('./account-stream.js');
const CONVERSATION_POLL_INTERVAL_MS = 2000;
const CONVERSATION_RATE_LIMIT_MAX_FALLBACK_MS = 30000;
const TURN_INACTIVITY_WATCHDOG_MS = 20000;

async function waitForConversation(
  session,
  token,
  conversationId,
  requestMessageId,
  sleep,
  reportProgress,
  execution
) {
  const endpoint = API.conversationById(conversationId);
  let consecutiveRateLimits = 0;
  let inactiveWaitMs = 0;
  let lastProgressKey = null;

  while (true) {
    let conversation;
    try {
      conversation = await apiJson(session, token, endpoint);
      consecutiveRateLimits = 0;
    } catch (error) {
      if (error?.status !== 429 || error?.endpoint !== endpoint)
        throw error;

      const fallbackDelay = Math.min(
        CONVERSATION_RATE_LIMIT_MAX_FALLBACK_MS,
        CONVERSATION_POLL_INTERVAL_MS * (2 ** Math.min(consecutiveRateLimits, 4))
      );
      const delay = Math.max(
        CONVERSATION_POLL_INTERVAL_MS,
        error.retryAfterMs ?? fallbackDelay
      );
      consecutiveRateLimits++;
      reportProgress?.({
        state: "rate-limited",
        detail: `ChatGPT rate limited state checks; retrying in ${Math.ceil(delay / 1000)}s.`
      });
      console.error(`ChatGPT conversation poll rate-limited; retrying in ${delay} ms.`);
      await sleep(delay);
      continue;
    }

    const turn = inspectConversationTurn(conversation, requestMessageId);
    if (turn.reply) {
      mergeExecutionMetadata(execution, turn.reply.execution, true);
      if (execution.model)
        turn.reply.model = modelSelectionId(execution.model, execution.thinkingEffort);
      turn.reply.execution = execution;
      reportProgress?.({
        state: "completed",
        detail: completionDetail(execution),
        analysis: turn.analysis
      });
      return turn.reply;
    }

    reportProgress?.({
      state: turn.state,
      detail: turn.detail,
      analysis: turn.analysis
    });

    const progressObserved = Boolean(
      turn.progressKey && turn.progressKey !== lastProgressKey
    );
    if (progressObserved)
      lastProgressKey = turn.progressKey;

    if (turn.active || progressObserved) {
      inactiveWaitMs = 0;
    } else if (inactiveWaitMs >= TURN_INACTIVITY_WATCHDOG_MS) {
      throw new Error(
        `ChatGPT showed no active generation for ${TURN_INACTIVITY_WATCHDOG_MS / 1000}s after the native request completed.`
      );
    }

    await sleep(CONVERSATION_POLL_INTERVAL_MS);
    if (!turn.active)
      inactiveWaitMs += CONVERSATION_POLL_INTERVAL_MS;
  }
}

function inspectConversationTurn(conversation, requestMessageId) {
  const reply = findVisibleAssistantReply(conversation, requestMessageId);
  if (reply)
    return {
      reply,
      active: false,
      progressKey: String(conversation?.current_node || reply.parentMessageId || ""),
      state: "responding",
      detail: "Model response completed.",
      analysis: collectTurnAnalysis(conversation, requestMessageId)
    };

  const mapping = conversation?.mapping || {};
  const progressKey = String(conversation?.current_node || "");
  let node = mapping[conversation?.current_node];
  let reachedRequest = false;
  let assistantObserved = false;
  let inProgress = null;
  const analysis = [];

  while (node) {
    const message = node.message;
    if (message?.id === requestMessageId) {
      reachedRequest = true;
      break;
    }

    if (message?.author?.role === "assistant") {
      assistantObserved = true;
      const channel = String(message.channel || "").trim().toLowerCase();
      if (channel === "analysis") {
        const text = visibleAssistantText(message);
        if (text) analysis.push(text);
      }
      if (!inProgress && message.status === "in_progress")
        inProgress = { channel, reasoning: hasReasoningMetadata(message) };
    }

    node = mapping[node.parent];
  }

  const analysisText = analysis.reverse().join("\n\n").trim() || null;
  if (!reachedRequest) {
    return {
      reply: null,
      active: false,
      progressKey: null,
      state: "waiting",
      detail: "Waiting for the submitted prompt to become the active ChatGPT turn.",
      analysis: null
    };
  }

  if (inProgress) {
    const thinking = inProgress.channel === "analysis" || inProgress.reasoning;
    return {
      reply: null,
      active: true,
      progressKey,
      state: thinking ? "thinking" : "responding",
      detail: thinking ? "Model is thinking." : "Model is generating a response.",
      analysis: analysisText
    };
  }

  if (assistantObserved) {
    return {
      reply: null,
      active: false,
      progressKey,
      state: "waiting",
      detail: "Model activity was observed, but no active generation is currently detected.",
      analysis: analysisText
    };
  }

  return {
    reply: null,
    active: false,
    progressKey,
    state: "waiting",
    detail: "Prompt is present, but no model activity has started yet.",
    analysis: null
  };
}

function collectTurnAnalysis(conversation, requestMessageId) {
  const mapping = conversation?.mapping || {};
  let node = mapping[conversation?.current_node];
  const values = [];
  while (node) {
    const message = node.message;
    if (message?.id === requestMessageId)
      return values.reverse().join("\n\n").trim() || null;
    if (message?.author?.role === "assistant" &&
        String(message.channel || "").trim().toLowerCase() === "analysis") {
      const text = visibleAssistantText(message);
      if (text) values.push(text);
    }
    node = mapping[node.parent];
  }
  return null;
}

function findVisibleAssistantReply(conversation, requestMessageId) {
  const mapping = conversation?.mapping || {};
  let node = mapping[conversation?.current_node];
  let assistant = null;
  const files = new Map();
  const execution = {};

  while (node) {
    const message = node.message;
    collectConversationExecutionMetadata(message, execution);

    if (message?.id === requestMessageId) {
      if (!assistant) return null;
      const requestResolvedModel = String(
        message.metadata?.resolved_model_slug || ""
      ).trim() || null;
      const resolvedModel = execution.model || requestResolvedModel;
      return {
        text: citedAssistantText(assistant),
        parentMessageId: assistant.id,
        projectId: conversation.gizmo_id || null,
        model: resolvedModel
          ? modelSelectionId(resolvedModel, execution.thinkingEffort)
          : null,
        execution,
        files
      };
    }

    collectFileRefs(message, files);
    if (!assistant && isVisibleAssistantMessage(message))
      assistant = message;
    node = mapping[node.parent];
  }

  return null;
}

function collectConversationExecutionMetadata(message, execution) {
  if (!message) return;
  collectToolMetadata(message, execution);
  if (message?.author?.role !== "assistant") return;
  collectMetadataFields(message.metadata || {}, execution);
  if (message?.content?.content_type === "reasoning_recap")
    execution.reasoningObserved = true;
}

function hasReasoningMetadata(message) {
  const metadata = message?.metadata || {};
  return Boolean(
    message?.content?.content_type === "reasoning_recap" ||
    String(metadata.reasoning_status || "").trim() ||
    finiteNumber(metadata.reasoning_start_time) !== null ||
    finiteNumber(metadata.reasoning_end_time) !== null
  );
}

function isVisibleAssistantMessage(message) {
  if (message?.author?.role !== "assistant" || message.status === "in_progress")
    return false;
  if (message.metadata?.is_visually_hidden_from_conversation === true || message.weight === 0)
    return false;

  const channel = String(message.channel || "").trim().toLowerCase();
  if (channel && channel !== "final")
    return false;

  const recipient = String(message.recipient || "").trim().toLowerCase();
  if (recipient && recipient !== "all")
    return false;

  const contentType = String(message.content?.content_type || "text").trim().toLowerCase();
  if (contentType !== "text" && contentType !== "multimodal_text")
    return false;

  return Boolean(visibleAssistantText(message));
}

function visibleAssistantText(message) {
  return (message?.content?.parts || [])
    .filter(part => typeof part === "string")
    .join("\n")
    .trim();
}

function citedAssistantText(message) {
  let text = visibleAssistantText(message);
  // ChatGPT embeds citation glyphs in text and provides their rendered
  // Markdown equivalents in metadata.content_references.
  const references = message?.metadata?.content_references;
  for (const reference of Array.isArray(references) ? references : []) {
    const marker = reference?.matched_text;
    const replacement = reference?.alt;
    if (typeof marker !== "string" || !/[\uE200-\uE203]/.test(marker) ||
        typeof replacement !== "string" || !marker)
      continue;
    text = text.split(marker).join(replacement);
  }

  // Keep missing citations visible as unresolved, never invent a source URL.
  return text
    .replace(/\uE200cite\uE202[^\uE201]*\uE201/g, "[citation unavailable]")
    .replace(/\uE200memcite\uE201/g, "")
    .trim();
}


module.exports = { waitForConversation };
