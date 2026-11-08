// SDK 0.3.300 Query controls return void: these are acknowledgements, not readback.
export async function configureRuntime(runtime, settings, state) {
  const confirmed = {};
  const errors = {};
  const apply = async (key, action) => {
    try { await action(); confirmed[key] = settings[key]; }
    catch (error) { errors[key] = String(error?.message ?? error); }
  };
  if (Object.hasOwn(settings, "model"))
    await apply("model", () => runtime.setModel(settings.model ?? undefined));
  if (Object.hasOwn(settings, "effort"))
    await apply("effort", () => runtime.applyFlagSettings({ effortLevel: settings.effort }));
  if (Object.hasOwn(settings, "planMode") || Object.hasOwn(settings, "permissionMode")) {
    const nextPlan = settings.planMode ?? state.planMode;
    const nextPermission = settings.permissionMode ?? state.permissionMode;
    try {
      await runtime.setPermissionMode(nextPlan === "on" ? "plan" :
        nextPermission === "auto" ? "bypassPermissions" : "default");
      state.planMode = nextPlan;
      state.permissionMode = nextPermission;
      confirmed.planMode = nextPlan;
      confirmed.permissionMode = nextPermission;
    } catch (error) {
      errors.permissionMode = String(error?.message ?? error);
      if (Object.hasOwn(settings, "planMode")) errors.planMode = errors.permissionMode;
    }
  }
  return { confirmed, errors };
}
