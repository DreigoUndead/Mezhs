The telemetry pipeline:

<svg viewBox="0 0 400 120" width="100%" height="120" role="img" aria-label="OpenTelemetry pipeline">
  <rect x="5" y="12" width="115" height="58" rx="6" fill="#e5f3e2" stroke="#477829" strokeWidth="2" />
  <text x="17" y="45" fontSize="15" fill="#333">Application</text>
  <line x1="120" y1="42" x2="170" y2="42" stroke="#333" strokeWidth="2" />
  <g>
    <rect x="170" y="12" width="110" height="58" rx="6" fill="#f3e5cf" />
    <text x="180" y="45">Collector</text>
  </g>
  <path d="M280 42 L310 42" stroke="#333" fill="none" />
  <text x="317" y="45">Elastic</text>
</svg>

End of diagram.