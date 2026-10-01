namespace ProspectStudio.Core.Reference;

/// <summary>
/// How the setup pipeline reports a finished step, so <c>prepare_data</c> can write progress to the
/// <c>jobs</c> table and the <c>setup</c> verb can print a line per step.
/// </summary>
/// <param name="step">What the step did.</param>
/// <param name="progress">0.0 to 1.0 across the whole pipeline.</param>
public delegate Task ReferenceStepReporter(
    ReferenceStepResult step,
    double progress,
    CancellationToken cancellationToken);
