const { ORIGIN, API, apiFetch } = require('./account-transport.js');

function nativePickerModels(catalog) {
  const result = [];
  const seen = new Set();
  const versions = Array.isArray(catalog?.versions) ? catalog.versions : [];

  for (const version of versions) {
    if (version?.enabled === false) continue;
    const versionName = String(
      version?.display_text_for_intelligence ||
      version?.display_text ||
      ""
    ).trim();
    const nativePresets = Array.isArray(version?.intelligence_presets)
      ? version.intelligence_presets
      : [];
    const presets = nativePresets.filter(preset =>
      preset?.preset_type === "available" &&
      preset?.enabled !== false
    );

    if (nativePresets.length) {
      for (const preset of presets) {
        const model = String(preset?.model_slug || "").trim();
        const effort = String(preset?.thinking_effort || "").trim();
        const presetName = String(
          preset?.selected_display_title ||
          preset?.title ||
          ""
        ).trim();
        addModelOption(
          result,
          seen,
          modelSelectionId(model, effort),
          [versionName, presetName].filter(Boolean).join(" · ")
        );
      }
      continue;
    }

    const model = (Array.isArray(version?.slugs) ? version.slugs : [])
      .map(value => String(value || "").trim())
      .find(Boolean);
    addModelOption(result, seen, model, versionName || model);
  }

  if (result.length)
    return result;

  for (const model of catalog?.models || []) {
    const id = String(model?.slug || model?.id || "").trim();
    const name = String(
      model?.title ||
      model?.display_name ||
      model?.name ||
      id
    ).trim();
    addModelOption(result, seen, id, name);
  }
  return result;
}

function addModelOption(result, seen, id, name) {
  const normalizedId = String(id || "").trim();
  const normalizedName = String(name || "").trim();
  const key = normalizedId.toLowerCase();
  if (!normalizedId || !normalizedName || seen.has(key)) return;
  seen.add(key);
  result.push({ id: normalizedId, name: normalizedName });
}

const MODEL_SELECTION_SEPARATOR = "::thinking-effort=";

function modelSelectionId(model, thinkingEffort) {
  return thinkingEffort
    ? `${model}${MODEL_SELECTION_SEPARATOR}${thinkingEffort}`
    : model;
}

function parseModelSelection(value) {
  const selected = String(value || "auto").trim() || "auto";
  const separator = selected.lastIndexOf(MODEL_SELECTION_SEPARATOR);
  return {
    model: separator > 0 ? selected.slice(0, separator) : selected,
    thinkingEffort: separator > 0
      ? selected.slice(separator + MODEL_SELECTION_SEPARATOR.length).trim() || null
      : null
  };
}


async function setModelPreference(session, token, selection) {
  if (!selection.model || selection.model === "auto") return;

  const url = new URL(API.modelPreference, ORIGIN);
  url.searchParams.set("model_slug", selection.model);
  if (selection.thinkingEffort)
    url.searchParams.set("thinking_effort", selection.thinkingEffort);
  await apiFetch(session, token, url.pathname + url.search, { method: "PATCH" });
}


module.exports = { nativePickerModels, modelSelectionId, parseModelSelection, setModelPreference };
