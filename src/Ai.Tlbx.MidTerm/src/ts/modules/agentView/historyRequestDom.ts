import { showDevErrorDialog } from '../../utils/devErrorDialog';
import {
  approveAppServerControlRequest,
  declineAppServerControlRequest,
  resolveAppServerControlUserInput,
  type AppServerControlHistoryRequestSummary,
} from '../../api/client';
import type { SessionAppServerControlViewState } from './types';

export type AgentHistoryRequestDomDeps = {
  getState: (sessionId: string) => SessionAppServerControlViewState | undefined;
  refreshAppServerControlSnapshot: (sessionId: string) => Promise<void>;
  renderCurrentAgentView: (sessionId: string) => void;
  logWarn: (message: () => string) => void;
};

type AppServerControlText = (key: string, fallback: string) => string;
type AppServerControlFormat = (
  key: string,
  fallback: string,
  replacements: Record<string, string | number>,
) => string;

export type ApprovalDetailPresentation = {
  summary: string | null;
  technicalDetail: string | null;
  tool: string | null;
};

export function parseApprovalDetail(detail: string | null | undefined): ApprovalDetailPresentation {
  const value = detail?.trim();
  if (!value) {
    return { summary: null, technicalDetail: null, tool: null };
  }

  const match = /^([^:\r\n]{1,80}):\s*(\{[\s\S]*\})$/.exec(value);
  if (!match) {
    return { summary: value, technicalDetail: null, tool: null };
  }
  const [, rawTool = '', rawInput = ''] = match;
  const tool = rawTool.trim();
  if (!tool || !rawInput) {
    return { summary: value, technicalDetail: null, tool: null };
  }

  try {
    const input = JSON.parse(rawInput) as Record<string, unknown>;
    const summary = readApprovalString(input, 'description', 'file_path', 'url', 'pattern');
    const technicalDetail =
      readApprovalString(input, 'command', 'cmd', 'path', 'file_path', 'url') ??
      JSON.stringify(input, null, 2);
    return {
      summary,
      technicalDetail: technicalDetail === summary ? null : technicalDetail,
      tool,
    };
  } catch {
    return { summary: value, technicalDetail: null, tool: null };
  }
}

function readApprovalString(input: Record<string, unknown>, ...keys: string[]): string | null {
  for (const key of keys) {
    const value = input[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return null;
}

export function createRequestActionBlock(args: {
  busy: boolean;
  deps: AgentHistoryRequestDomDeps;
  appServerControlFormat: AppServerControlFormat;
  appServerControlText: AppServerControlText;
  request: AppServerControlHistoryRequestSummary;
  sessionId: string;
  surface: 'composer' | 'history';
  state: SessionAppServerControlViewState;
}): HTMLElement {
  const {
    busy,
    deps,
    appServerControlFormat,
    appServerControlText,
    request,
    sessionId,
    state,
    surface,
  } = args;
  const actions = document.createElement('div');
  const isInterviewRequest = request.kind === 'interview' && request.questions.length > 0;
  actions.className = `agent-request-actions agent-request-actions-${surface} ${isInterviewRequest ? 'agent-request-actions-user-input' : 'agent-request-actions-approval'}`;
  const panel = document.createElement('section');
  panel.className = `agent-request-panel agent-request-panel-${surface} ${isInterviewRequest ? 'agent-request-panel-user-input' : 'agent-request-panel-approval'} ${request.state === 'open' ? 'agent-request-panel-open' : 'agent-request-panel-resolved'}`;
  panel.appendChild(
    createRequestPanelHeader(request, appServerControlText, appServerControlFormat),
  );

  if (request.state !== 'open') {
    panel.appendChild(createResolvedRequestSummary(request, appServerControlText));
    actions.appendChild(panel);
    return actions;
  }

  if (isInterviewRequest) {
    panel.appendChild(
      createUserInputRequestForm({
        busy,
        deps,
        appServerControlText,
        request,
        sessionId,
        state,
      }),
    );
    actions.appendChild(panel);
    return actions;
  }

  panel.appendChild(createApprovalButtonRow(busy, deps, appServerControlText, request, sessionId));
  actions.appendChild(panel);
  return actions;
}

function createUserInputRequestForm(args: {
  busy: boolean;
  deps: AgentHistoryRequestDomDeps;
  appServerControlText: AppServerControlText;
  request: AppServerControlHistoryRequestSummary;
  sessionId: string;
  state: SessionAppServerControlViewState;
}): HTMLElement {
  const { busy, deps, appServerControlText, request, sessionId, state } = args;
  const draftAnswers = ensureRequestDraftAnswers(state, request);
  const activeQuestionIndex = resolveActiveRequestQuestionIndex(state, request);
  const activeQuestion = request.questions[activeQuestionIndex];
  const form = document.createElement('form');
  form.className = 'agent-request-form';
  if (!activeQuestion) {
    return form;
  }

  form.appendChild(
    createQuestionField({
      deps,
      draftAnswers,
      index: activeQuestionIndex,
      appServerControlText,
      question: activeQuestion,
      request,
      sessionId,
      totalQuestions: request.questions.length,
    }),
  );
  form.appendChild(
    createUserInputControls(
      busy,
      deps,
      appServerControlText,
      request,
      sessionId,
      activeQuestion,
      activeQuestionIndex,
      draftAnswers,
    ),
  );
  // Draft edits keep the focused input node alive. The virtualized row does
  // not need a structural rerender just to enable its existing submit button.
  const syncSubmit = (): void => {
    const submit = form.querySelector<HTMLButtonElement>('button[type="submit"]');
    if (submit) {
      submit.disabled =
        state.requestBusyIds.has(request.requestId) ||
        !hasDraftAnswerForQuestion(ensureRequestDraftAnswers(state, request), activeQuestion);
    }
  };
  form.addEventListener('input', syncSubmit);
  form.addEventListener('change', syncSubmit);
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (activeQuestionIndex < request.questions.length - 1) {
      setActiveRequestQuestionIndex(deps, sessionId, request.requestId, activeQuestionIndex + 1);
      return;
    }

    void handleResolveUserInput(
      deps,
      appServerControlText,
      sessionId,
      request.requestId,
      collectQuestionAnswers(state, request),
    );
  });
  return form;
}

function createUserInputControls(
  busy: boolean,
  deps: AgentHistoryRequestDomDeps,
  appServerControlText: AppServerControlText,
  request: AppServerControlHistoryRequestSummary,
  sessionId: string,
  activeQuestion: AppServerControlHistoryRequestSummary['questions'][number],
  activeQuestionIndex: number,
  draftAnswers: Record<string, string[]>,
): HTMLElement {
  const formControls = document.createElement('div');
  formControls.className = 'agent-request-button-row';

  if (activeQuestionIndex > 0) {
    const back = document.createElement('button');
    back.type = 'button';
    back.className = 'agent-view-btn';
    back.disabled = busy;
    back.textContent = appServerControlText('appServerControl.request.back', 'Back');
    back.addEventListener('click', () => {
      setActiveRequestQuestionIndex(deps, sessionId, request.requestId, activeQuestionIndex - 1);
    });
    formControls.appendChild(back);
  }

  const submit = document.createElement('button');
  submit.type = 'submit';
  submit.className = 'agent-view-btn agent-view-btn-primary';
  submit.disabled = busy || !hasDraftAnswerForQuestion(draftAnswers, activeQuestion);
  submit.textContent =
    activeQuestionIndex < request.questions.length - 1
      ? appServerControlText('appServerControl.request.continue', 'Continue')
      : busy
        ? appServerControlText('appServerControl.request.sending', 'Sending…')
        : appServerControlText('appServerControl.request.sendAnswer', 'Send answer');
  formControls.appendChild(submit);

  return formControls;
}

function createApprovalButtonRow(
  busy: boolean,
  deps: AgentHistoryRequestDomDeps,
  appServerControlText: AppServerControlText,
  request: AppServerControlHistoryRequestSummary,
  sessionId: string,
): HTMLElement {
  const buttonRow = document.createElement('div');
  buttonRow.className = 'agent-request-button-row';

  const approve = document.createElement('button');
  approve.type = 'button';
  approve.className = 'agent-view-btn agent-view-btn-primary';
  approve.disabled = busy;
  approve.textContent = busy
    ? appServerControlText('appServerControl.request.working', 'Working…')
    : appServerControlText('appServerControl.request.approveOnce', 'Allow');
  approve.addEventListener('click', () => {
    void runRequestAction(deps, appServerControlText, sessionId, request.requestId, () =>
      approveAppServerControlRequest(sessionId, request.requestId),
    );
  });

  const decline = document.createElement('button');
  decline.type = 'button';
  decline.className = 'agent-view-btn';
  decline.disabled = busy;
  decline.textContent = appServerControlText('appServerControl.request.decline', 'Decline');
  decline.addEventListener('click', () => {
    void runRequestAction(deps, appServerControlText, sessionId, request.requestId, () =>
      declineAppServerControlRequest(sessionId, request.requestId),
    );
  });

  buttonRow.append(decline, approve);
  return buttonRow;
}

function createRequestPanelHeader(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
  appServerControlFormat: AppServerControlFormat,
): HTMLElement {
  const header = document.createElement('div');
  header.className = 'agent-request-panel-header';
  const topRow = document.createElement('div');
  topRow.className = 'agent-request-panel-topline';

  const eyebrow = document.createElement('span');
  eyebrow.className = 'agent-request-eyebrow';
  eyebrow.textContent = summarizeRequestEyebrow(request, appServerControlText);
  topRow.appendChild(eyebrow);

  const approvalDetail = request.kind === 'interview' ? null : parseApprovalDetail(request.detail);
  if (approvalDetail?.tool) {
    const tool = document.createElement('span');
    tool.className = 'agent-request-tool';
    tool.textContent = approvalDetail.tool;
    topRow.appendChild(tool);
  }

  header.appendChild(topRow);

  const summary = document.createElement('p');
  summary.className = 'agent-request-summary';
  summary.textContent =
    request.state === 'open' && approvalDetail
      ? (approvalDetail.summary ??
        appServerControlFormat(
          'appServerControl.request.toolAction',
          '{tool} wants to perform an action.',
          {
            tool:
              approvalDetail.tool ??
              appServerControlText('appServerControl.request.toolFallback', 'The agent'),
          },
        ))
      : summarizeRequestInterruption(request, appServerControlText, appServerControlFormat);
  if (!isCancelledRequest(request)) {
    header.appendChild(summary);
  }

  if (approvalDetail?.technicalDetail) {
    const disclosure = document.createElement('details');
    disclosure.className = 'agent-request-technical';
    const label = document.createElement('summary');
    label.textContent = appServerControlText(
      'appServerControl.request.technicalDetails',
      'Technical details',
    );
    const detail = document.createElement('pre');
    detail.textContent = approvalDetail.technicalDetail;
    disclosure.append(label, detail);
    header.appendChild(disclosure);
  } else if (
    request.kind === 'interview' &&
    request.questions.length === 0 &&
    request.detail?.trim()
  ) {
    const detail = document.createElement('p');
    detail.className = 'agent-request-detail';
    detail.textContent = request.detail;
    header.appendChild(detail);
  }

  return header;
}

function summarizeRequestEyebrow(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
): string {
  if (isCancelledRequest(request)) {
    return appServerControlText('appServerControl.request.cancelled', 'Cancelled');
  }
  if (request.kind === 'interview') {
    return request.state === 'open'
      ? appServerControlText('appServerControl.request.pendingUserInput', 'Input required')
      : appServerControlText('appServerControl.request.completedInterview', 'Answer submitted');
  }

  return request.state === 'open'
    ? appServerControlText('appServerControl.request.approvalRequired', 'Approval required')
    : appServerControlText('appServerControl.request.completedApproval', 'Approval resolved');
}

function isCancelledRequest(request: AppServerControlHistoryRequestSummary): boolean {
  return request.state === 'cancelled' || request.decision === 'cancel';
}

function summarizeRequestInterruption(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
  appServerControlFormat: AppServerControlFormat,
): string {
  if (request.state !== 'open') {
    const resolvedDecision =
      request.decision?.trim() ||
      appServerControlText('appServerControl.request.resolved', 'resolved');
    return request.kind === 'interview'
      ? appServerControlText(
          'appServerControl.request.interviewResolved',
          'The requested answers were submitted.',
        )
      : appServerControlFormat(
          'appServerControl.request.approvalResolved',
          'Request resolved as {decision}.',
          {
            decision: resolvedDecision,
          },
        );
  }

  if (request.kind === 'interview') {
    const activeQuestion = request.questions[0];
    if (request.questions.length === 1 && activeQuestion?.options.length) {
      if (activeQuestion.allowCustomAnswer) {
        return activeQuestion.multiSelect
          ? appServerControlText(
              'appServerControl.request.selectManyOrCustom',
              'Select options or enter your own answer.',
            )
          : appServerControlText(
              'appServerControl.request.selectOneOrCustom',
              'Select an option or enter your own answer.',
            );
      }
      return activeQuestion.multiSelect
        ? appServerControlFormat(
            'appServerControl.request.selectManyToContinue',
            'Select one or more of {count} options to continue.',
            { count: activeQuestion.options.length },
          )
        : appServerControlFormat(
            'appServerControl.request.selectOneToContinue',
            'Select 1 of {count} options to continue.',
            { count: activeQuestion.options.length },
          );
    }

    return request.questions.length === 1
      ? appServerControlText(
          'appServerControl.request.needsOneAnswer',
          'The agent needs one answer to continue.',
        )
      : appServerControlFormat(
          'appServerControl.request.needsManyAnswers',
          'The agent needs {count} answers to continue.',
          { count: request.questions.length },
        );
  }

  const label =
    request.kindLabel.trim() ||
    appServerControlText('appServerControl.request.approvalLabel', 'Approval');
  return appServerControlFormat(
    'appServerControl.request.requiredBeforeContinue',
    '{label} required before the turn can continue.',
    { label },
  );
}

function createResolvedRequestSummary(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
): HTMLElement {
  return request.kind === 'interview'
    ? createResolvedInterviewSummary(request, appServerControlText)
    : createResolvedApprovalSummary(request, appServerControlText);
}

function createResolvedInterviewSummary(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
): HTMLElement {
  const wrapper = document.createElement('div');
  wrapper.className = 'agent-request-answer-list';

  if (request.answers.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'agent-request-answer-empty';
    empty.textContent = appServerControlText(
      'appServerControl.request.noAnswersRecorded',
      'No answers were recorded.',
    );
    wrapper.appendChild(empty);
    return wrapper;
  }

  for (const question of request.questions) {
    const answer = request.answers.find((candidate) => candidate.questionId === question.id);
    const item = document.createElement('section');
    item.className = 'agent-request-answer-item';

    const label = document.createElement('div');
    label.className = 'agent-request-answer-label';
    label.textContent = question.header.trim() || question.question;
    item.appendChild(label);

    const value = document.createElement('div');
    value.className = 'agent-request-answer-value';
    const answers = answer?.answers.filter((entry) => entry.trim().length > 0) ?? [];
    value.textContent =
      answers.length > 0
        ? question.isSecret
          ? '••••••••'
          : answers
              .map(
                (answer) =>
                  question.options.find((option) => option.value === answer)?.label ?? answer,
              )
              .join(', ')
        : appServerControlText('appServerControl.request.answerSkipped', 'No answer submitted');
    item.appendChild(value);

    wrapper.appendChild(item);
  }

  return wrapper;
}

function createResolvedApprovalSummary(
  request: AppServerControlHistoryRequestSummary,
  appServerControlText: AppServerControlText,
): HTMLElement {
  const wrapper = document.createElement('div');
  wrapper.className = 'agent-request-answer-list';

  const item = document.createElement('section');
  item.className = 'agent-request-answer-item';

  const label = document.createElement('div');
  label.className = 'agent-request-answer-label';
  label.textContent = appServerControlText('appServerControl.request.decisionLabel', 'Decision');
  item.appendChild(label);

  const value = document.createElement('div');
  value.className = 'agent-request-answer-value';
  value.textContent =
    request.decision?.trim() ||
    appServerControlText('appServerControl.request.resolved', 'Resolved');
  item.appendChild(value);

  wrapper.appendChild(item);
  return wrapper;
}

function createQuestionField(args: {
  deps: AgentHistoryRequestDomDeps;
  draftAnswers: Record<string, string[]>;
  index: number;
  appServerControlText: AppServerControlText;
  question: AppServerControlHistoryRequestSummary['questions'][number];
  request: AppServerControlHistoryRequestSummary;
  sessionId: string;
  totalQuestions: number;
}): HTMLElement {
  const {
    deps,
    draftAnswers,
    index,
    appServerControlText,
    question,
    request,
    sessionId,
    totalQuestions,
  } = args;
  const wrapper = document.createElement('section');
  wrapper.className = 'agent-request-field';

  if (question.header && question.header.trim()) {
    const header = document.createElement('div');
    header.className = 'agent-request-field-header';
    const fieldHeader = document.createElement('span');
    fieldHeader.className = 'agent-request-field-label';
    fieldHeader.textContent = question.header;
    header.appendChild(fieldHeader);
    wrapper.appendChild(header);
  }

  const title = document.createElement('p');
  title.className = 'agent-request-question';
  title.textContent = question.question;
  wrapper.appendChild(title);

  const draftValue = draftAnswers[question.id] ?? [];
  if (question.options.length > 0 && question.multiSelect) {
    wrapper.appendChild(
      createQuestionChoiceList({
        autoAdvance: false,
        deps,
        inputType: 'checkbox',
        appServerControlText,
        question,
        request,
        selectedAnswers: draftValue,
        sessionId,
      }),
    );
    return wrapper;
  }

  if (question.options.length > 0) {
    wrapper.appendChild(
      createQuestionChoiceList({
        autoAdvance: index < totalQuestions - 1,
        deps,
        inputType: 'radio',
        appServerControlText,
        question,
        request,
        selectedAnswers: draftValue,
        sessionId,
      }),
    );
    return wrapper;
  }

  const input = document.createElement('input');
  input.type = question.isSecret ? 'password' : 'text';
  if (question.isSecret) {
    input.autocomplete = 'off';
    input.spellcheck = false;
  }
  input.name = question.id;
  input.className = 'agent-request-input';
  input.placeholder = appServerControlText('appServerControl.request.typeAnswer', 'Type answer');
  input.value = draftValue[0] || '';
  input.addEventListener('input', () => {
    updateRequestDraftAnswers(
      deps,
      sessionId,
      request.requestId,
      question.id,
      [question.isSecret ? input.value : input.value.trim()],
      false,
    );
  });
  wrapper.appendChild(input);
  return wrapper;
}

function createQuestionChoiceList(args: {
  autoAdvance: boolean;
  deps: AgentHistoryRequestDomDeps;
  inputType: 'checkbox' | 'radio';
  appServerControlText: AppServerControlText;
  question: AppServerControlHistoryRequestSummary['questions'][number];
  request: AppServerControlHistoryRequestSummary;
  selectedAnswers: readonly string[];
  sessionId: string;
}): HTMLElement {
  const {
    autoAdvance,
    deps,
    inputType,
    question,
    request,
    selectedAnswers,
    sessionId,
    appServerControlText,
  } = args;
  const options = document.createElement('div');
  options.className = 'agent-request-choice-list';

  for (const [index, option] of question.options.entries()) {
    const optionValue = option.value ?? option.label;
    const optionLabel = document.createElement('label');
    optionLabel.className = 'agent-request-choice';
    const input = document.createElement('input');
    input.type = inputType;
    input.name = question.id;
    input.value = optionValue;
    input.className = 'agent-request-choice-input';
    input.checked = selectedAnswers.includes(optionValue);
    input.addEventListener('change', () => {
      if (inputType === 'radio') {
        const custom = options.querySelector<HTMLInputElement>('.agent-request-custom-input');
        if (custom) custom.value = '';
        updateRequestDraftAnswers(
          deps,
          sessionId,
          request.requestId,
          question.id,
          [optionValue],
          false,
        );
        if (autoAdvance) {
          const currentIndex =
            deps.getState(sessionId)?.requestQuestionIndexById[request.requestId] ?? 0;
          setActiveRequestQuestionIndex(deps, sessionId, request.requestId, currentIndex + 1);
          return;
        }

        deps.renderCurrentAgentView(sessionId);
        return;
      }

      const nextAnswers = Array.from(
        options.querySelectorAll<HTMLInputElement>(
          `input[name="${CSS.escape(question.id)}"]:checked`,
        ),
      ).map((candidate) => candidate.value);
      const custom = options.querySelector<HTMLInputElement>('.agent-request-custom-input');
      if (custom?.value.trim())
        nextAnswers.push(question.isSecret ? custom.value : custom.value.trim());
      updateRequestDraftAnswers(deps, sessionId, request.requestId, question.id, nextAnswers);
    });
    optionLabel.appendChild(input);

    if (index < 9) {
      const shortcut = document.createElement('span');
      shortcut.className = 'agent-request-choice-shortcut';
      shortcut.textContent = String(index + 1);
      optionLabel.appendChild(shortcut);
    }

    const copy = document.createElement('span');
    copy.className = 'agent-request-choice-copy';
    const title = document.createElement('span');
    title.className = 'agent-request-choice-title';
    title.textContent = option.label;
    copy.appendChild(title);
    if (option.description && option.description !== option.label) {
      const description = document.createElement('span');
      description.className = 'agent-request-choice-description';
      description.textContent = option.description;
      copy.appendChild(description);
    }

    optionLabel.appendChild(copy);
    options.appendChild(optionLabel);
  }

  if (question.allowCustomAnswer) {
    const custom = document.createElement('input');
    custom.type = question.isSecret ? 'password' : 'text';
    custom.name = question.id + ':custom';
    if (question.isSecret) {
      custom.autocomplete = 'off';
      custom.spellcheck = false;
    }
    custom.className = 'agent-request-input agent-request-custom-input';
    const otherLabel = appServerControlText('appServerControl.request.otherAnswer', 'Other answer');
    custom.placeholder = otherLabel;
    custom.setAttribute('aria-label', `${question.header || question.question}: ${otherLabel}`);
    custom.value =
      selectedAnswers.find(
        (answer) => !question.options.some((option) => (option.value ?? option.label) === answer),
      ) ?? '';
    custom.addEventListener('input', () => {
      const choices = Array.from(options.querySelectorAll<HTMLInputElement>('input:checked'));
      if (!question.multiSelect && custom.value.trim()) {
        for (const choice of choices) choice.checked = false;
      }
      const answers =
        question.multiSelect || !custom.value.trim() ? choices.map((choice) => choice.value) : [];
      if (custom.value.trim()) answers.push(question.isSecret ? custom.value : custom.value.trim());
      updateRequestDraftAnswers(deps, sessionId, request.requestId, question.id, answers, false);
    });
    options.appendChild(custom);
  }
  return options;
}

function collectQuestionAnswers(
  state: SessionAppServerControlViewState,
  request: AppServerControlHistoryRequestSummary,
): Array<{ questionId: string; answers: string[] }> {
  const draftAnswers = ensureRequestDraftAnswers(state, request);
  return request.questions.map((question) => ({
    questionId: question.id,
    answers: (draftAnswers[question.id] ?? []).filter(Boolean),
  }));
}

function resolveActiveRequestQuestionIndex(
  state: SessionAppServerControlViewState,
  request: AppServerControlHistoryRequestSummary,
): number {
  const maxIndex = Math.max(0, request.questions.length - 1);
  const currentIndex = state.requestQuestionIndexById[request.requestId] ?? 0;
  return Math.max(0, Math.min(currentIndex, maxIndex));
}

function setActiveRequestQuestionIndex(
  deps: AgentHistoryRequestDomDeps,
  sessionId: string,
  requestId: string,
  nextIndex: number,
): void {
  const state = deps.getState(sessionId);
  if (!state) {
    return;
  }

  state.requestQuestionIndexById[requestId] = Math.max(0, nextIndex);
  deps.renderCurrentAgentView(sessionId);
}

function ensureRequestDraftAnswers(
  state: SessionAppServerControlViewState,
  request: AppServerControlHistoryRequestSummary,
): Record<string, string[]> {
  const existing = state.requestDraftAnswersById[request.requestId];
  if (existing) {
    for (const question of request.questions) {
      existing[question.id] ??= [];
    }
    return existing;
  }

  const nextDraft: Record<string, string[]> = {};
  for (const question of request.questions) {
    nextDraft[question.id] = [];
  }

  state.requestDraftAnswersById[request.requestId] = nextDraft;
  return nextDraft;
}

function updateRequestDraftAnswers(
  deps: AgentHistoryRequestDomDeps,
  sessionId: string,
  requestId: string,
  questionId: string,
  answers: string[],
  rerender = true,
): void {
  const state = deps.getState(sessionId);
  if (!state) {
    return;
  }

  const requestDrafts = state.requestDraftAnswersById[requestId] ?? {};
  requestDrafts[questionId] = answers.filter((answer) => answer.trim().length > 0);
  state.requestDraftAnswersById[requestId] = requestDrafts;
  if (rerender) {
    deps.renderCurrentAgentView(sessionId);
  }
}

function hasDraftAnswerForQuestion(
  draftAnswers: Record<string, string[]>,
  question: AppServerControlHistoryRequestSummary['questions'][number],
): boolean {
  return (draftAnswers[question.id] ?? []).some((answer) => answer.trim().length > 0);
}

async function handleResolveUserInput(
  deps: AgentHistoryRequestDomDeps,
  appServerControlText: AppServerControlText,
  sessionId: string,
  requestId: string,
  answers: Array<{ questionId: string; answers: string[] }>,
): Promise<void> {
  await runRequestAction(deps, appServerControlText, sessionId, requestId, () =>
    resolveAppServerControlUserInput(sessionId, requestId, {
      answers: answers.filter((answer) => answer.answers.length > 0),
    }),
  );
}

async function runRequestAction(
  deps: AgentHistoryRequestDomDeps,
  appServerControlText: AppServerControlText,
  sessionId: string,
  requestId: string,
  action: () => Promise<unknown>,
): Promise<void> {
  const state = deps.getState(sessionId);
  if (!state || state.requestBusyIds.has(requestId)) {
    return;
  }

  state.requestBusyIds.add(requestId);
  deps.renderCurrentAgentView(sessionId);
  try {
    await action();
    await deps.refreshAppServerControlSnapshot(sessionId);
  } catch (error) {
    deps.logWarn(
      () =>
        `Failed to resolve AppServerControl request ${requestId} for ${sessionId}: ${String(error)}`,
    );
    showDevErrorDialog({
      title: appServerControlText(
        'appServerControl.error.requestTitle',
        'AppServerControl request failed',
      ),
      context: `AppServerControl request action failed for session ${sessionId}, request ${requestId}`,
      error,
    });
  } finally {
    state.requestBusyIds.delete(requestId);
    deps.renderCurrentAgentView(sessionId);
  }
}
