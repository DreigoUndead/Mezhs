3. What should change

I agree with your proposed architectural boundary:

<box border radius="lg" padding={3} gap={2}>
  <box background="surface-secondary" radius="md" padding={3} align="center">
    **Canonical tag parser**

    <text color="secondary" size="xs">Identify known tags, match boundaries, recurse according to registry</text>
  </box>
  <row justify="center">
    <icon name="arrow-down" color="secondary" size="lg"/>
  </row>
  <grid columns={2} gap={2}>
    <grid-item>
      <box background="surface-secondary" padding={3} radius="md" gap={1} align="center">
        **Known elements**
        <text color="secondary" size="xs" textAlign="center">Validated typed nodes and host commands</text>
      </box>
    </grid-item>
    <grid-item>
      <box background="surface-secondary" padding={3} radius="md" gap={1} align="center">
        **Ordinary text**
        <text color="secondary" size="xs" textAlign="center">Markdown formatting only</text>
      </box>
    </grid-item>
  </grid>
</box>
