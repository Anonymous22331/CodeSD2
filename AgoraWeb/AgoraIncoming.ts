import { extensionManager } from "@docsvision/webclient/System/ExtensionManager";
import { MessageBox } from "@docsvision/webclient/Helpers/MessageBox/MessageBox";
import { $DigitalSignature } from "@docsvision/webclient/BackOffice/$DigitalSignature";
import { $DepartmentController, $DirectoryDesignerRowController, $EmployeeController, $LayoutCardController, $SignatureController } from "@docsvision/webclient/Generated/DocsVision.WebClient.Controllers";
import type { CheckBox } from "@docsvision/webclient/Platform/CheckBox";
import type { DirectoryDesignerRow } from "@docsvision/webclient/BackOffice/DirectoryDesignerRow";
import type { FileListControl } from "@docsvision/webclient/BackOffice/FileListControl";
import type { Numerator } from "@docsvision/webclient/BackOffice/Numerator";
import type { PrintButton } from "@docsvision/webclient/BackOffice/PrintButton";
import type { StaffDirectoryItemsMultiple, StaffDirectoryItemsSingle } from "@docsvision/webclient/BackOffice/StaffDirectoryItems";
import type { StateButtons } from "@docsvision/webclient/BackOffice/StateButtons";
import type { Tasks } from "@docsvision/webclient/BackOffice/Tasks";
import { GenModels } from "@docsvision/webclient/Generated/DocsVision.WebClient.Models";
import type { Block } from "@docsvision/webclient/Platform/Block";
import type { CustomButton } from "@docsvision/webclient/Platform/CustomButton";
import type { DateTimePicker } from "@docsvision/webclient/Platform/DateTimePicker";
import type { TextArea } from "@docsvision/webclient/Platform/TextArea";
import type { TextBox } from "@docsvision/webclient/Platform/TextBox";
import { EditMode } from "@docsvision/webclient/System/EditMode";
import type { Layout } from "@docsvision/webclient/System/Layout";
import type { ICardStateChangingEventArgs } from "@docsvision/webclient/System/ICardStateChangingEventArgs";
import type { ICancelableEventArgs } from "@docsvision/webclient/System/ICancelableEventArgs";
import type { ILayoutSavedEventArgs } from "@docsvision/webclient/System/ILayoutParams";
import type { LocationContainerControl } from "@docsvision/webclient/System/LocationContainerControl";
import { $CurrentEmployeeId } from "@docsvision/webclient/StandardServices";

const preparationStateId = "de445cf0-2482-48ae-80b3-49746540c4de";
const registeredStateId = "ed171ce1-7014-45ac-9a30-223a6c8c62e6";
const incomingKindId = "9530f638-6669-48b8-867a-a135ce4232a6";
const approvalReviewKindId = "38c5329a-dd34-44ed-a5a6-000000000028";
const viewLayoutMode = "c4c5e860-191b-47b3-a865-6ccc1fac636a";
const registerIncomingOperationId = "aa43e10d-2d81-4f9c-8eb0-6beddd02b216f";
const sendForReviewOperationId = "882f04d1-7521-4945-a54f-86a73e05e03b";
const sendForWorkOperationId = "07e1e86c-0fc5-473b-a6e8-7738bf025f28";
const printDocumentOperation = "d66380f0-e629-40d3-80a4-646976b1a0a1";
const editTaskTreeOperation = "4a6cd4cc-68f2-456a-854a-4a8fd786596d";
const sendOperations = new WeakMap<StateButtons, GenModels.OperationDataModel[]>();
const sendingLayouts = new WeakSet<Layout>();

/** Настраивает видимость блоков и заполняет регистрационные реквизиты. */
function agoraIncomingOpened(layout: Layout): void {
    const register = layout.controls.tryGet<CustomButton>("agoraRegisterIncoming");
    if (register && layout.cardInfo.state?.stateId?.toLowerCase() === preparationStateId) {
        const value = layout.controls.tryGet<StaffDirectoryItemsSingle | StaffDirectoryItemsMultiple>("registrar")?.params.value;
        const registrar = Array.isArray(value) ? value[0] : value;
        const currentEmployeeId = layout.getService($CurrentEmployeeId);
        register.params.visibility = layout.editOperations.available(registerIncomingOperationId)
            || Boolean(registrar?.id && registrar.id.toLowerCase() === currentEmployeeId?.toLowerCase());
    }
    const review = layout.controls.get<CheckBox>("agoraRequiresReview");
    if (layout.layoutInfo.action === GenModels.LayoutAction.Create) {
        prepareDirectExecutionOnCreate(review);
    }
    agoraReviewChanged(review);
    void fillRegistrationDetails(layout);
}

/** Перед первой отправкой новой формы фиксирует «Нет» через штатное событие флага. */
function prepareDirectExecutionOnCreate(review: CheckBox): void {
    requestAnimationFrame(() => {
        const field = document.querySelector<HTMLInputElement>('[data-control-name="agoraRequiresReview"] input[type="checkbox"]');
        if (!field) return;

        const selector = '[data-button-name="save-layout"]';
        let form = field.parentElement;
        while (form && !form.querySelector(selector)) form = form.parentElement;
        const buttons = form?.querySelectorAll<HTMLButtonElement>(selector);
        if (!buttons?.length) return;

        const prepare = (event: MouseEvent) => {
            for (const button of buttons) button.removeEventListener("click", prepare, true);
            if (field.checked || field.disabled || review.value === true) return;

            event.preventDefault();
            event.stopImmediatePropagation();
            const button = event.currentTarget as HTMLButtonElement;
            field.click();
            requestAnimationFrame(() => {
                if (field.checked) field.click();
                requestAnimationFrame(() => button.click());
            });
        };
        for (const button of buttons) button.addEventListener("click", prepare, true);
    });
}

/** Заполняет место регистрации и предлагает журнал подразделения из справочника. */
async function fillRegistrationDetails(layout: Layout): Promise<void> {
    const registrationPlace = layout.controls.tryGet<TextBox>("agoraRegistrationPlace");
    const registrarControl = layout.controls.get<StaffDirectoryItemsSingle>("registrar");
    const registrar = registrarControl.params.value;
    const employee = Array.isArray(registrar) ? registrar[0] : registrar;
    const employeeId = employee?.id ?? layout.getService($CurrentEmployeeId);
    if (!registrationPlace || !employeeId) {
        return;
    }

    try {
        if (!employee?.id && layout.layoutInfo.action !== GenModels.LayoutAction.View) {
            const current = await layout.getService($EmployeeController).getEmployee(employeeId);
            if (!registrarControl.params.value && current) {
                registrarControl.params.value = current;
            }
        }
        agoraReviewChanged(layout.controls.get<CheckBox>("agoraRequiresReview"));
        const department = await layout.getService($DepartmentController).getStaffEmployeeDepartment(employeeId);
        registrationPlace.params.value = department?.name ?? "";
        const journal = layout.controls.tryGet<DirectoryDesignerRow>("agoraJournal");
        const isPreparation = layout.cardInfo.state?.stateId?.toLowerCase() === preparationStateId;
        if ((layout.layoutInfo.action !== GenModels.LayoutAction.Create && !isPreparation) || !department || !journal?.params.itemType
            || journal.params.value || journal.params.disabled || journal.params.editMode !== EditMode.Edit) {
            return;
        }

        const dictionary = await journal.getService($DirectoryDesignerRowController).loadDirectoryData({
            nodeId: journal.params.itemType,
            searchArea: GenModels.DirectoryDesignerSearchArea.OnlyNode,
            searchItemsType: GenModels.DirectoryDesignerSearchItemsType.Rows,
            searchText: "",
            pagination: {},
            needBuildPath: false
        });
        const departmentNames = [department.name, department.fullName].filter(Boolean).map(normalizeDepartmentName);
        const matchingRows = dictionary.items.filter(item => item.description
            && departmentNames.includes(normalizeDepartmentName(item.description)));
        if (matchingRows.length === 1 && !journal.params.value) {
            journal.params.value = matchingRows[0];
        }
    } catch (error) {
        console.warn("Агора: не удалось подобрать журнал по подразделению регистратора.", error);
    }
}

/** Сопоставляет наименования подразделений из справочников Агора и сотрудников. */
function normalizeDepartmentName(name: string): string {
    return name.trim().replace(/\s+/g, " ").toLocaleLowerCase("ru-RU");
}

/** Применяет права из ролевой модели к печати и созданию заданий в дереве. */
function agoraApplyIncomingPermissions(layout: Layout): void {
    const print = layout.controls.tryGet<PrintButton>("printButton");
    if (print && !layout.editOperations.available(printDocumentOperation)) {
        print.params.visibility = false;
    }

    const tasks = layout.controls.tryGet<Tasks>("createTasks");
    if (tasks && !layout.editOperations.available(editTaskTreeOperation)) {
        tasks.params.canCreateTask = false;
        tasks.params.canCreateTaskGroup = false;
    }
}

/** Показывает реквизиты прямого исполнения или блок резолюции по выбранному маршруту. */
function agoraReviewChanged(review: CheckBox): void {
    const controls = review.layout.controls;
    const requiresReview = review.params.value === true;
    controls.get<DirectoryDesignerRow>("agoraExecutionType").params.visibility = !requiresReview;
    controls.get<StaffDirectoryItemsMultiple>("agoraCoexecutors").params.visibility = !requiresReview;
    const resolution = controls.tryGet<Block>("agoraResolutionBlock");
    const inspector = controls.get<StaffDirectoryItemsSingle>("agoraInspector");
    if (!requiresReview && !inspector.params.value && !inspector.params.disabled && inspector.params.editMode === EditMode.Edit) {
        const registrar = controls.get<StaffDirectoryItemsSingle | StaffDirectoryItemsMultiple>("registrar").params.value;
        inspector.params.value = Array.isArray(registrar) ? registrar[0] : registrar;
    }
    if (resolution) {
        const readers = controls.get<StaffDirectoryItemsMultiple>("agoraAcquaintance");
        if (requiresReview) {
            controls.get<StaffDirectoryItemsSingle>("agoraResolutionInspector").params.value = inspector.params.value;
            controls.get<StaffDirectoryItemsMultiple>("agoraResolutionReaders").params.value = readers.params.value ?? [];
        }
        inspector.params.visibility = !requiresReview;
        readers.params.visibility = !requiresReview;
        resolution.params.visibility = requiresReview;
    } else {
        controls.get<StaffDirectoryItemsMultiple>("agoraAssignedPerformers").params.visibility = requiresReview;
        controls.get<DirectoryDesignerRow>("agoraResolutionType").params.visibility = requiresReview;
        controls.get<TextArea>("agoraResolutionText").params.visibility = requiresReview;
    }
    const buttons = controls.tryGet<StateButtons>("stateButtons");
    const send = controls.tryGet<CustomButton>("agoraSendIncoming");
    if (send) {
        const operation = requiresReview ? sendForReviewOperationId : sendForWorkOperationId;
        send.params.visibility = review.layout.cardInfo.state?.stateId?.toLowerCase() === registeredStateId
            && review.layout.editOperations.available(operation);
        if (buttons?.params.operations) {
            buttons.params.operations = buttons.params.operations.filter(operation => !isSendOperation(operation));
        }
    } else if (buttons?.params.operations) {
        showSendOperation(buttons, requiresReview);
    }
}

/** Сохраняет выбор контролера из блока резолюции в привязанном реквизите письма. */
function agoraResolutionInspectorChanged(control: StaffDirectoryItemsSingle): void {
    control.layout.controls.get<StaffDirectoryItemsSingle>("agoraInspector").params.value = control.params.value;
}

/** Сохраняет выбор ознакомителей из блока резолюции в привязанном реквизите письма. */
function agoraResolutionReadersChanged(control: StaffDirectoryItemsMultiple): void {
    control.layout.controls.get<StaffDirectoryItemsMultiple>("agoraAcquaintance").params.value = control.params.value ?? [];
}

/** Показывает единственную доступную команду отправки для выбранной ветки. */
function showSendOperation(buttons: StateButtons, requiresReview: boolean): void {
    const desiredId = requiresReview ? sendForReviewOperationId : sendForWorkOperationId;
    const current = buttons.params.operations;
    const otherOperations = current.filter(operation => !isSendOperation(operation));
    const available = sendOperations.get(buttons) ?? current.filter(isSendOperation);
    sendOperations.set(buttons, available);
    const desired = available.find(operation => operation.operationId?.toLowerCase() === desiredId);
    if (desired) {
        otherOperations.push({ ...desired, displayName: "Отправить в работу" });
    }
    buttons.params.operations = otherOperations;
}

/** Определяет операции отправки, которые выполняет серверный обработчик Агора. */
function isSendOperation(operation: GenModels.OperationDataModel): boolean {
    const id = operation.operationId?.toLowerCase();
    return id === sendForReviewOperationId || id === sendForWorkOperationId;
}

/** Переносит формулировку выбранной типовой резолюции в редактируемый текст. */
function agoraResolutionSelected(resolution: DirectoryDesignerRow): void {
    const chosen = resolution.params.value;
    if (chosen?.name) {
        resolution.layout.controls.get<TextArea>("agoraResolutionText").params.value = chosen.name;
    }
}

/** Проверяет основной файл и регистрационный номер перед переходом состояния. */
function agoraCheckMainFile(layout: Layout, args: ICancelableEventArgs<ICardStateChangingEventArgs>): void {
    const operation = args.data;
    if (operation && isSendOperation(operation)) {
        args.cancel();
        void sendIncoming(layout);
        return;
    }
    if (operation?.operationId?.toLowerCase() !== registerIncomingOperationId) {
        return;
    }
    validateRegistration(layout, args);
}

/** Проверяет файл и запускает штатный переход «Зарегистрировать» из просмотра письма. */
async function agoraRegisterIncomingFromCard(button: CustomButton): Promise<void> {
    if (button.params.isLoading) return;
    const layout = button.layout;
    if (!hasMainFile(layout)) {
        await MessageBox.ShowWarning("ВАЖНО! Вложите файл!");
        return;
    }
    button.params.isLoading = true;
    try {
        if (!layout.controls.tryGet<Numerator>("regNumber")?.params.value?.number) {
            await ensureIncomingNumber(layout);
        }
        const response = await fetch("/api/agora/incoming/register", {
            method: "POST",
            redirect: "error",
            headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
            body: JSON.stringify({ cardId: layout.cardInfo.id, timestamp: layout.cardInfo.timestamp })
        });
        if (response.status === 422) {
            await MessageBox.ShowWarning("ВАЖНО! Вложите файл!");
        } else if (response.ok) {
            await layout.reloadFromServer();
        } else {
            throw new Error("Registration failed.");
        }
    } catch {
        await MessageBox.ShowWarning("Не удалось зарегистрировать письмо. Обновите карточку и повторите попытку.");
    } finally {
        button.params.isLoading = false;
    }
}

/** Проверяет наличие основного файла в загруженном списке вложений. */
function hasMainFile(layout: Layout): boolean {
    return layout.controls.get<FileListControl>("fileList").params.files?.some(file => file.data.isMain) ?? false;
}

/** Блокирует регистрацию без основного файла и получает регистрационный номер. */
function validateRegistration(layout: Layout, args: ICancelableEventArgs<GenModels.OperationDataModel>): void {
    if (!hasMainFile(layout)) {
        args.cancel();
        MessageBox.ShowWarning("ВАЖНО! Вложите файл!");
        return;
    }
    if (!layout.controls.tryGet<Numerator>("regNumber")?.params.value?.number) {
        args.wait();
        void ensureIncomingNumber(layout).then(() => args.accept(), () => {
            args.cancel();
            void MessageBox.ShowWarning("Не удалось получить регистрационный номер. Проверьте журнал и повторите.");
        });
    }
}

/** Сохраняет регистратора и актуализирует номер письма в подготовке. */
function agoraNumberAfterSave(layout: Layout, args: ICancelableEventArgs<ILayoutSavedEventArgs>): void {
    const state = layout.cardInfo.state?.stateId?.toLowerCase();
    if (state && state !== preparationStateId) {
        return;
    }

    args.wait();
    void ensureIncomingNumber(layout).then(() => args.accept(), () => {
        args.accept();
        void MessageBox.ShowWarning("Письмо сохранено. Проверьте журнал и сохраните карточку ещё раз, чтобы получить номер.");
    });
}

/** Запрашивает номер у сервера и обновляет реквизиты открытой формы. */
async function ensureIncomingNumber(layout: Layout): Promise<void> {
    const response = await fetch("/api/agora/incoming/number", {
        method: "POST",
        redirect: "error",
        headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
        body: JSON.stringify({ cardId: layout.cardInfo.id })
    });
    if (!response.ok) {
        throw new Error("Could not assign the incoming number.");
    }

    const { id, number, timestamp } = await response.json() as GenModels.NumberInfo & { timestamp: number };
    const control = layout.controls.tryGet<Numerator>("regNumber");
    if (control?.params.value) {
        control.params.value = { ...control.params.value, id, number };
    } else {
        control?.setNumberText(number);
    }
    layout.cardInfo.timestamp = timestamp;
}

/** Передаёт письмо в обработчик маршрута и загружает новое состояние карточки. */
async function sendIncoming(layout: Layout): Promise<void> {
    if (sendingLayouts.has(layout)) {
        return;
    }

    sendingLayouts.add(layout);
    try {
        const response = await fetch("/api/agora/incoming/send", {
            method: "POST",
            redirect: "error",
            headers: { "Content-Type": "application/json", "X-Requested-With": "XMLHttpRequest" },
            body: JSON.stringify({ cardId: layout.cardInfo.id, timestamp: layout.cardInfo.timestamp })
        });
        if (!response.ok) {
            const message = response.status === 403
                ? "У вас нет права отправить это письмо в работу."
                : "Не удалось отправить письмо. Обновите карточку и повторите попытку.";
            await MessageBox.ShowWarning(message);
            return;
        }
        await layout.reloadFromServer();
    } catch {
        await MessageBox.ShowWarning("Проверьте состояние письма после обновления карточки.");
    } finally {
        sendingLayouts.delete(layout);
    }
}

/** Отправляет зарегистрированное письмо из формы просмотра. */
function agoraSendIncomingFromCard(button: CustomButton): void {
    void sendIncoming(button.layout);
}

/** Проверяет реквизиты решения и запрашивает подтверждение, когда оно требуется по BRD. */
function agoraBeforeReviewDecision(layout: Layout, args: ICancelableEventArgs<ICardStateChangingEventArgs>): void {
    if (layout.cardInfo.kindId?.toLowerCase() !== approvalReviewKindId) {
        validateReviewDecision(layout, args, false);
    }
}

/** Сохраняет резолюцию до формирования запроса штатной кнопки решения. */
function agoraBeforeApprovalReviewClick(buttons: StateButtons, args: ICancelableEventArgs<GenModels.OperationDataModel>): void {
    validateReviewDecision(buttons.layout, args, true);
}

/** Проверяет решение рассмотрения и сохраняет поля, редактируемые в разметке просмотра. */
function validateReviewDecision(layout: Layout, args: ICancelableEventArgs<GenModels.OperationDataModel>, saveForm: boolean): void {
    const decision = args.data?.additionalInfo?.decisionName || args.data?.displayName;
    if (decision !== "Рассмотрено" && decision !== "Оставить себе" && decision !== "Оставить без рассмотрения") {
        return;
    }

    const controls = layout.controls;
    const type = controls.get<DirectoryDesignerRow>("agoraResolutionType").params.value;
    const text = controls.get<TextArea>("agoraResolutionText").params.value;
    const people = controls.get<StaffDirectoryItemsMultiple>("agoraReviewPerformers").params.value ?? [];
    const controller = controls.get<StaffDirectoryItemsSingle>("agoraReviewController").params.value;
    const deadline = controls.tryGet<DateTimePicker>("agoraReviewDeadline")?.params.value;
    if (decision !== "Оставить без рассмотрения" && !controller) {
        args.cancel();
        MessageBox.ShowWarning("Укажите контролёра.");
        return;
    }
    if (decision === "Рассмотрено") {
        if (!type || !text?.trim() || !people.length || !deadline) {
            args.cancel();
            MessageBox.ShowWarning("Укажите тип и текст резолюции, назначьте исполнителей и заполните срок исполнения.");
            return;
        }
        args.wait();
        void confirmReviewDeadline(layout, deadline).then(async () => {
            if (saveForm) await layout.saveCard();
        }).then(() => args.accept(), () => args.cancel());
        return;
    }
    const consequence = decision === "Оставить себе"
        ? "вам как единственному исполнителю будет поставлено задание «На исполнение»."
        : "рассмотрение письма будет завершено.";
    args.wait();
    const confirmed = type || text?.trim() || people.length || deadline ? MessageBox.ShowConfirmation(
        "Просьба подтвердить, что ранее заполненные данные по исполнителям, срокам и тексту резолюции не актуальны. " +
            "Данные будут удалены, и " + consequence,
        decision,
        "Подтверждаю",
        "Отмена"
    ) : Promise.resolve();
    void confirmed.then(async () => {
        if (saveForm) await layout.saveCard();
    }).then(() => args.accept(), () => args.cancel());
}

/** Сверяет введённый срок резолюции с сохранённым сроком связанного письма. */
async function confirmReviewDeadline(layout: Layout, deadline: Date): Promise<void> {
    const linked = layout.controls.tryGet<LocationContainerControl>("mainDocLocationContainer")?.params.mountedLayout;
    let expected: unknown = linked?.controls.tryGet<DateTimePicker>("agoraControlDate")?.params.value;
    if (expected == null && linked?.cardInfo?.kindId?.toLowerCase() === incomingKindId) {
        try {
            const view = await layout.getService($LayoutCardController).view(linked.cardInfo.id, viewLayoutMode);
            expected = readDocumentDeadline(view.layoutModel);
        } catch {
            expected = undefined;
        }
    }

    const value = expected instanceof Date ? expected : typeof expected === "string" ? new Date(expected) : undefined;
    const minutes = (date: Date) => Math.floor(new Date(date).getTime() / 60000);
    if (value && Number.isFinite(minutes(value)) && minutes(value) === minutes(deadline)) {
        return;
    }
    const warning = value && Number.isFinite(minutes(value))
        ? "Срок в резолюции отличается от срока исполнения письма. Сохранить решение с новым сроком?"
        : "Не удалось сверить сроки резолюции и письма. Сохранить решение?";
    await MessageBox.ShowConfirmation(warning, "Срок исполнения", "Сохранить решение", "Вернуться к резолюции");
}

/** Находит контрольный срок среди вложенных элементов разметки письма. */
function readDocumentDeadline(model: unknown): unknown {
    if (!model || typeof model !== "object") {
        return undefined;
    }
    const control = model as {
        name?: string;
        value?: unknown;
        binding?: { value?: unknown };
        properties?: { name?: string; value?: unknown; binding?: { value?: unknown } };
    };
    if ((control.name ?? control.properties?.name) === "agoraControlDate") {
        return control.value ?? control.binding?.value ?? control.properties?.value ?? control.properties?.binding?.value;
    }
    for (const child of Object.values(model)) {
        const value = readDocumentDeadline(child);
        if (value != null) {
            return value;
        }
    }
    return undefined;
}

/** Показывает действие подписания только в задании с доступными решениями рассмотрения. */
function agoraSetReviewSigningVisibility(layout: Layout): void {
    const button = layout.controls.tryGet<CustomButton>("agoraSignIncoming");
    const stateButtons = layout.controls.tryGet<StateButtons>("stateButtons");
    const canReview = stateButtons?.params.operations?.some(operation =>
        ["Рассмотрено", "Оставить себе", "Оставить без рассмотрения"].includes(
            operation.additionalInfo?.decisionName || operation.displayName)) ?? false;
    if (button) button.params.visibility = canReview;
    if (layout.cardInfo.kindId?.toLowerCase() === approvalReviewKindId) {
        const fields = ["agoraResolutionType", "agoraResolutionText", "agoraReviewDeadline",
            "agoraReviewPerformers", "agoraReviewController", "agoraReviewReaders"];
        for (const name of fields) {
            const field = layout.controls.tryGet<DirectoryDesignerRow | TextArea | DateTimePicker |
                StaffDirectoryItemsSingle | StaffDirectoryItemsMultiple>(name);
            if (field) field.params.disabled = !canReview;
        }
    }
}

/** Открывает штатный диалог криптографического подписания связанного входящего письма. */
async function agoraSignIncomingFromReview(button: CustomButton): Promise<void> {
    if (button.params.isLoading) {
        return;
    }

    const layout = button.layout;
    const document = layout.controls.tryGet<LocationContainerControl>("mainDocLocationContainer")
        ?.params.mountedLayout?.cardInfo;
    if (!document || document.kindId?.toLowerCase() !== incomingKindId) {
        await MessageBox.ShowWarning("В задании не найдено связанное входящее письмо Агора.");
        return;
    }

    button.params.isLoading = true;
    try {
        if (!await layout.getService($SignatureController).canAddSignature(document.id)) {
            await MessageBox.ShowWarning("У вас нет права подписать связанное письмо в его текущем состоянии.");
            return;
        }
        await layout.getService($DigitalSignature).showDocumentSignDialog(document.id, {
            sourceCardInfo: document,
            dialogProps: { hideSimpleSign: true, dialogHeaderText: "Подписать входящее письмо" }
        });
    } finally {
        button.params.isLoading = false;
    }
}

extensionManager.registerExtension({
    name: "AgoraIncoming",
    version: "2.6",
    globalEventHandlers: [{
        agoraIncomingOpened, agoraReviewChanged, agoraResolutionSelected, agoraCheckMainFile, agoraBeforeReviewDecision, agoraBeforeApprovalReviewClick,
        agoraNumberAfterSave, agoraApplyIncomingPermissions, agoraResolutionInspectorChanged, agoraResolutionReadersChanged,
        agoraSetReviewSigningVisibility, agoraSignIncomingFromReview, agoraSendIncomingFromCard, agoraRegisterIncomingFromCard
    }]
});
