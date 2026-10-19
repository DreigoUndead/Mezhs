const { randomUUID } = require('node:crypto');
const { ORIGIN, API, requireToken, apiFetch, apiJson, webApiHeaders, clientContext, nativeChatRequirementsHeaders, isConversationUnavailable } = require('./account-transport.js');
const { parseModelSelection, setModelPreference } = require('./account-models.js');
const { uploadFiles, downloadFiles } = require('./account-files.js');
const { readConversationStream, mergeExecutionMetadata } = require('./account-stream.js');
const { waitForConversation } = require('./account-results.js');

async function sendAccountMessage(context, isNew) {
  const token = await requireToken(context.session);
  const selection = parseModelSelection(context.args.model);
  await setModelPreference(context.session, token, selection);
  return sendApiAccountMessage(context, isNew, token, selection);
}


async function sendApiAccountMessage({ window, session, args, sleep, reportProgress }, isNew, token, selection) {
  const uploaded = await uploadFiles(session, token, args.files || []);
  const imageParts = uploaded
    .filter(file => file.contentType.startsWith("image/"))
    .map(file => ({
      content_type: "image_asset_pointer",
      asset_pointer: `file-service://${file.id}`,
      size_bytes: file.size,
      width: file.width,
      height: file.height
    }));
  const attachments = uploaded.map(file => ({
    id: file.id,
    name: file.name,
    mimeType: file.contentType,
    size: file.size,
    ...(file.width ? { width: file.width, height: file.height } : {})
  }));
  const metadata = {
    selected_sources: [],
    serialization_metadata: { custom_symbol_offsets: [] },
    submission_mode: "manual_send",
    ...(attachments.length ? { attachments } : {})
  };
  const projectMode = isNew && args.projectId
    ? { kind: "gizmo_interaction", gizmo_id: args.projectId }
    : isNew
      ? { kind: "primary_assistant" }
      : undefined;
  const parentMessageId = isNew ? "client-created-root" : args.parentMessageId;

  function buildPayload(messageId, conversationId) {
    const payload = {
      action: "next",
      conversation_id: conversationId,
      messages: [{
        id: messageId,
        author: { role: "user" },
        create_time: Date.now() / 1000,
        content: {
          content_type: imageParts.length ? "multimodal_text" : "text",
          parts: [...imageParts, String(args.prompt || "")]
        },
        metadata
      }],
      model: selection.model,
      parent_message_id: parentMessageId,
      client_prepare_state: "success",
      timezone_offset_min: new Date().getTimezoneOffset(),
      timezone: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC",
      conversation_mode: projectMode,
      enable_message_followups: true,
      system_hints: [],
      supports_buffering: true,
      supported_encodings: ["v1"],
      client_contextual_info: clientContext(),
      paragen_cot_summary_display_override: "allow",
      force_parallel_switch: "auto",
      local_function_names: ["local.continue_in_work"]
    };
    if (selection.thinkingEffort)
      payload.thinking_effort = selection.thinkingEffort;
    return payload;
  }

  reportProgress?.({
    state: "submitting",
    detail: "Submitting prompt to ChatGPT."
  });

  const clientSessionId = randomUUID();
  const execution = {
    requestedModel: selection.model === "auto" ? null : selection.model,
    requestedThinkingEffort: selection.thinkingEffort
  };
  if (isNew && args.projectId) {
    const initialized = await initializeConversation(
      session,
      token,
      args.projectId,
      clientSessionId
    );
    const initializedModel = String(
      initialized?.intended_default_model_slug ||
      initialized?.default_model_slug ||
      ""
    ).trim();
    if (initializedModel)
      execution.initializedDefaultModel = initializedModel;
  }

  const requestMessageId = randomUUID();
  const posted = await postConversationTurn(
    window,
    session,
    token,
    buildPayload(requestMessageId, isNew ? undefined : args.conversationId),
    args.conversationId,
    reportProgress,
    clientSessionId
  );
  let conversationId = posted.conversationId;
  mergeExecutionMetadata(execution, posted.execution);
  if (!conversationId)
    throw new Error("ChatGPT did not return a conversation id.");

  return completeAccountMessage(
    session, token, conversationId, requestMessageId, sleep, isNew,
    reportProgress, execution
  );
}

async function postConversationTurn(
  window,
  session,
  token,
  payload,
  fallbackConversationId = null,
  reportProgress,
  clientSessionId
) {
  const turnTraceId = randomUUID();
  const conduitToken = await getConduitToken(
    session,
    token,
    turnTraceId,
    conversationPreparePayload(payload),
    clientSessionId
  );
  // The authenticated frontend owns Sentinel requirements and enforcement tokens.
  // Never invent or reuse tokens from a different turn/session.
  const sentinelHeaders = await nativeChatRequirementsHeaders(window, session, token);

  const headers = await webApiHeaders(
    session,
    API.conversation,
    clientSessionId,
    {
      "Content-Type": "application/json",
      "Accept": "text/event-stream",
      ...sentinelHeaders,
      "x-conduit-token": conduitToken,
      "x-oai-turn-trace-id": turnTraceId
    }
  );

  const response = await apiFetch(session, token, API.conversation, {
    method: "POST",
    headers,
    body: JSON.stringify(payload)
  });
  reportProgress?.({
    state: "waiting",
    detail: "Prompt accepted; waiting for model activity."
  });
  let stream;
  try {
    stream = await readConversationStream(response, reportProgress);
  } catch (error) {
    if (!fallbackConversationId)
      throw new Error(`ChatGPT stream failed after submission; delivery may have succeeded, so the turn must not be resent: ${error.message}`);
    reportProgress?.({ state: "waiting", detail: "Stream interrupted; reconciling the existing conversation." });
    stream = { conversationId: fallbackConversationId, execution: {} };
  }
  return {
    conversationId: stream.conversationId || fallbackConversationId,
    execution: stream.execution
  };
}


async function completeAccountMessage(
  session,
  token,
  conversationId,
  requestMessageId,
  sleep,
  isNew,
  reportProgress,
  execution
) {
  let result;
  try {
    result = await waitForConversation(
      session,
      token,
      conversationId,
      requestMessageId,
      sleep,
      reportProgress,
      execution
    );
  } catch (error) {
    if (!isNew && isConversationUnavailable(error, conversationId))
      return { conversationUnavailable: true };
    throw error;
  }

  return {
    text: result.text,
    conversationId,
    parentMessageId: result.parentMessageId,
    projectId: result.projectId,
    chatUrl: `${ORIGIN}/c/${conversationId}`,
    artifacts: await downloadFiles(session, token, result.files),
    model: result.model
  };
}

function conversationPreparePayload(payload) {
  const message = payload.messages?.at(-1);
  const prepared = {
    action: payload.action,
    conversation_id: payload.conversation_id,
    parent_message_id: payload.parent_message_id || "client-created-root",
    model: payload.model,
    client_prepare_state: "none",
    client_prepare_dispatch: "debounced",
    client_prepare_source: "composer_editor_state",
    timezone_offset_min: payload.timezone_offset_min,
    timezone: payload.timezone,
    conversation_mode: payload.conversation_mode || { kind: "primary_assistant" },
    system_hints: payload.system_hints || [],
    partial_query: message ? {
      id: message.id,
      author: message.author,
      content: message.content.content_type === "multimodal_text"
        ? { content_type: "text", parts: message.content.parts.filter(part => typeof part === "string") }
        : message.content
    } : undefined,
    supports_buffering: payload.supports_buffering,
    supported_encodings: payload.supported_encodings,
    client_contextual_info: {
      app_name: payload.client_contextual_info?.app_name || "chatgpt.com",
      has_web_push_capabilities: Boolean(
        payload.client_contextual_info?.has_web_push_capabilities
      ),
      web_push_notification_permission:
        payload.client_contextual_info?.web_push_notification_permission || "default"
    },
    local_function_names: payload.local_function_names || []
  };
  if (payload.thinking_effort)
    prepared.thinking_effort = payload.thinking_effort;
  return prepared;
}

async function initializeConversation(
  session,
  token,
  projectId,
  clientSessionId
) {
  const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC";
  const headers = await webApiHeaders(
    session,
    API.conversationInit,
    clientSessionId,
    {
      "Accept": "*/*",
      "Content-Type": "application/json"
    }
  );
  return apiJson(session, token, API.conversationInit, {
    method: "POST",
    headers,
    body: JSON.stringify({
      gizmo_id: projectId,
      requested_default_model: null,
      conversation_id: null,
      timezone,
      timezone_offset_min: new Date().getTimezoneOffset(),
      conversation_origin: null
    })
  });
}

async function getConduitToken(
  session,
  token,
  turnTraceId,
  body,
  clientSessionId
) {
  const headers = await webApiHeaders(
    session,
    API.conversationPrepare,
    clientSessionId,
    {
      "Accept": "*/*",
      "Content-Type": "application/json",
      "x-oai-turn-trace-id": turnTraceId
    }
  );
  const response = await apiJson(session, token, API.conversationPrepare, {
    method: "POST",
    headers,
    body: JSON.stringify(body)
  });
  const conduitToken = String(response?.conduit_token || "").trim();
  if (!conduitToken)
    throw new Error("ChatGPT conversation prepare did not return a conduit token.");
  return conduitToken;
}


module.exports = { sendAccountMessage, postConversationTurn, conversationPreparePayload };
