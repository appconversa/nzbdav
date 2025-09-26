import { Button, Form } from "react-bootstrap";
import styles from "./usenet.module.css";
import {
    useCallback,
    useEffect,
    useMemo,
    useState,
    type Dispatch,
    type SetStateAction,
} from "react";

type UsenetSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
    onReadyToSave: (isReadyToSave: boolean) => void;
};

type ProviderFormState = {
    name: string;
    host: string;
    port: string;
    useSsl: boolean;
    user: string;
    pass: string;
    connections: string;
};

const blankProvider = (index: number): ProviderFormState => ({
    name: `Provider ${index + 1}`,
    host: "",
    port: "",
    useSsl: true,
    user: "",
    pass: "",
    connections: "",
});

const providerHash = (provider: ProviderFormState) => [
    provider.name,
    provider.host,
    provider.port,
    provider.useSsl ? "true" : "false",
    provider.user,
    provider.pass,
    provider.connections,
].join("|");

const parseProvidersFromConfig = (config: Record<string, string>): ProviderFormState[] => {
    const providers: ProviderFormState[] = [];
    const rawProviders = config["usenet.providers"];
    if (rawProviders) {
        try {
            const parsed = JSON.parse(rawProviders);
            if (Array.isArray(parsed)) {
                parsed.forEach((value: any, index: number) => {
                    providers.push({
                        name: typeof value?.name === "string" && value.name.trim() !== ""
                            ? value.name
                            : `Provider ${index + 1}`,
                        host: stringFromUnknown(value?.host),
                        port: stringFromUnknown(value?.port),
                        useSsl: parseBoolean(value?.useSsl),
                        user: stringFromUnknown(value?.user),
                        pass: stringFromUnknown(value?.pass),
                        connections: stringFromUnknown(value?.connections),
                    });
                });
            }
        } catch {
            /* ignore malformed provider JSON */
        }
    }

    if (providers.length > 0) {
        return providers;
    }

    return [
        {
            name: config["usenet.host"] ? "Primary" : "Provider 1",
            host: config["usenet.host"] || "",
            port: config["usenet.port"] || "",
            useSsl: config["usenet.use-ssl"] === "true",
            user: config["usenet.user"] || "",
            pass: config["usenet.pass"] || "",
            connections: config["usenet.connections"] || "",
        },
    ];
};

const stringFromUnknown = (value: unknown): string => {
    if (value === null || value === undefined) {
        return "";
    }
    if (typeof value === "string") {
        return value;
    }
    if (typeof value === "number") {
        return Number.isFinite(value) ? String(value) : "";
    }
    return "";
};

const parseBoolean = (value: unknown): boolean => {
    if (typeof value === "boolean") {
        return value;
    }
    if (typeof value === "string") {
        return value.trim().toLowerCase() === "true";
    }
    return false;
};

const stringifyProviders = (providers: ProviderFormState[]): string =>
    JSON.stringify(
        providers.map(provider => ({
            name: provider.name,
            host: provider.host,
            port: provider.port,
            useSsl: provider.useSsl,
            user: provider.user,
            pass: provider.pass,
            connections: provider.connections,
        })),
    );

const computeTotalConnections = (providers: ProviderFormState[]): number =>
    providers.reduce((sum, provider) => {
        const connections = Number.parseInt(provider.connections, 10);
        if (Number.isFinite(connections)) {
            return sum + connections;
        }
        return sum;
    }, 0);

const validateProvider = (provider: ProviderFormState): string | null =>
    !provider.host
        ? "`Host` is required"
        : !provider.port
            ? "`Port` is required"
            : !isPositiveInteger(provider.port)
                ? "`Port` is invalid"
                : !provider.user
                    ? "`User` is required"
                    : !provider.pass
                        ? "`Pass` is required"
                        : !provider.connections
                            ? "`Max Connections` is required"
                            : !isPositiveInteger(provider.connections)
                                ? "`Max Connections` is invalid"
                                : null;

const getConnectionsPerStreamError = (
    value: string,
    totalConnections: number,
): string | null => {
    if (!value) {
        return "`Connections Per Stream` is required";
    }
    if (!isPositiveInteger(value)) {
        return "`Connections Per Stream` is invalid";
    }
    const perStream = Number(value);
    if (totalConnections > 0 && perStream > totalConnections) {
        return "`Connections Per Stream` is invalid";
    }
    return null;
};

export function UsenetSettings({ config, setNewConfig, onReadyToSave }: UsenetSettingsProps) {
    const providers = useMemo(() => parseProvidersFromConfig(config), [config]);
    const [testedProviders, setTestedProviders] = useState<Record<string, boolean>>({});
    const [testingIndex, setTestingIndex] = useState<number | null>(null);

    const totalConnections = useMemo(() => computeTotalConnections(providers), [providers]);
    const connectionsPerStream = config["usenet.connections-per-stream"] || "";
    const connectionsPerStreamError = useMemo(
        () => getConnectionsPerStreamError(connectionsPerStream, totalConnections),
        [connectionsPerStream, totalConnections],
    );

    useEffect(() => {
        const allProvidersValid = providers.every(provider => validateProvider(provider) === null);
        const allProvidersTested = providers.every(provider => testedProviders[providerHash(provider)] === true);
        const isReady =
            providers.length > 0 &&
            allProvidersValid &&
            allProvidersTested &&
            !connectionsPerStreamError;
        onReadyToSave(isReady);
    }, [providers, testedProviders, connectionsPerStreamError, onReadyToSave]);

    const applyProviders = useCallback((nextProviders: ProviderFormState[]) => {
        const providersToSave = nextProviders.length > 0 ? nextProviders : [blankProvider(0)];
        const serializedProviders = stringifyProviders(providersToSave);
        const firstProvider = providersToSave[0];
        const total = computeTotalConnections(providersToSave);

        setNewConfig(prev => ({
            ...prev,
            "usenet.providers": serializedProviders,
            "usenet.host": firstProvider.host,
            "usenet.port": firstProvider.port,
            "usenet.use-ssl": firstProvider.useSsl ? "true" : "false",
            "usenet.user": firstProvider.user,
            "usenet.pass": firstProvider.pass,
            "usenet.connections": total > 0 ? String(total) : "",
        }));
    }, [setNewConfig]);

    const updateProvider = useCallback((index: number, updates: Partial<ProviderFormState>) => {
        const nextProviders = providers.map((provider, providerIndex) =>
            providerIndex === index ? { ...provider, ...updates } : provider,
        );
        applyProviders(nextProviders);
    }, [providers, applyProviders]);

    const addProvider = useCallback(() => {
        const nextProviders = [...providers, blankProvider(providers.length)];
        applyProviders(nextProviders);
    }, [providers, applyProviders]);

    const removeProvider = useCallback((index: number) => {
        const nextProviders = providers.filter((_, providerIndex) => providerIndex !== index);
        applyProviders(nextProviders);
    }, [providers, applyProviders]);

    const onTestProvider = useCallback(async (index: number) => {
        const provider = providers[index];
        if (!provider) {
            return;
        }

        const validationMessage = validateProvider(provider);
        if (validationMessage) {
            return;
        }

        const hash = providerHash(provider);
        setTestingIndex(index);
        try {
            const form = new FormData();
            form.append("host", provider.host);
            form.append("port", provider.port);
            form.append("use-ssl", provider.useSsl ? "true" : "false");
            form.append("user", provider.user);
            form.append("pass", provider.pass);

            const response = await fetch("/api/test-usenet-connection", {
                method: "POST",
                body: form,
            });
            const success = response.ok && ((await response.json())?.connected === true);
            setTestedProviders(prev => ({ ...prev, [hash]: success }));
        } finally {
            setTestingIndex(null);
        }
    }, [providers]);

    return (
        <div className={styles.container}>
            <div className={styles.providersContainer}>
                {providers.map((provider, index) => {
                    const validationMessage = validateProvider(provider);
                    const hash = providerHash(provider);
                    const testStatus = testedProviders[hash];
                    const isTesting = testingIndex === index;

                    let testButtonLabel: string;
                    if (isTesting) {
                        testButtonLabel = "Testing Connection...";
                    } else if (validationMessage) {
                        testButtonLabel = validationMessage;
                    } else if (testStatus === true) {
                        testButtonLabel = "Connected ✅";
                    } else if (testStatus === false) {
                        testButtonLabel = "Test Connection ❌";
                    } else {
                        testButtonLabel = "Test Connection";
                    }

                    const testButtonVariant = isTesting
                        ? "secondary"
                        : testStatus === true
                            ? "success"
                            : testStatus === false
                                ? "danger"
                                : "primary";

                    const isTestEnabled = !validationMessage && !isTesting;

                    return (
                        <div key={`${index}-${provider.name}`} className={styles.providerCard}>
                            <div className={styles.providerHeader}>
                                <Form.Group className={styles["form-group"]}>
                                    <Form.Label>Display Name</Form.Label>
                                    <Form.Control
                                        type="text"
                                        value={provider.name}
                                        onChange={event => updateProvider(index, { name: event.target.value })}
                                    />
                                </Form.Group>
                                {providers.length > 1 && (
                                    <Button
                                        variant="outline-danger"
                                        size="sm"
                                        onClick={() => removeProvider(index)}
                                        className={styles.removeProviderButton}
                                    >
                                        Remove
                                    </Button>
                                )}
                            </div>

                            <Form.Group className={styles["form-group"]}>
                                <Form.Label>Host</Form.Label>
                                <Form.Control
                                    type="text"
                                    className={styles.input}
                                    value={provider.host}
                                    onChange={event => updateProvider(index, { host: event.target.value })}
                                />
                            </Form.Group>

                            <Form.Group className={styles["form-group"]}>
                                <Form.Label>Port</Form.Label>
                                <Form.Control
                                    type="text"
                                    className={styles.input}
                                    value={provider.port}
                                    onChange={event => updateProvider(index, { port: event.target.value })}
                                />
                            </Form.Group>

                            <div className={styles["justify-right"]}>
                                <Form.Check
                                    type="checkbox"
                                    label="Use SSL"
                                    checked={provider.useSsl}
                                    onChange={event => updateProvider(index, { useSsl: Boolean(event.target.checked) })}
                                />
                            </div>

                            <Form.Group className={styles["form-group"]}>
                                <Form.Label>User</Form.Label>
                                <Form.Control
                                    type="text"
                                    className={styles.input}
                                    value={provider.user}
                                    onChange={event => updateProvider(index, { user: event.target.value })}
                                />
                            </Form.Group>

                            <Form.Group className={styles["form-group"]}>
                                <Form.Label>Pass</Form.Label>
                                <Form.Control
                                    type="password"
                                    className={styles.input}
                                    value={provider.pass}
                                    onChange={event => updateProvider(index, { pass: event.target.value })}
                                />
                            </Form.Group>

                            <Form.Group className={styles["form-group"]}>
                                <Form.Label>Max Connections</Form.Label>
                                <Form.Control
                                    type="text"
                                    className={styles.input}
                                    value={provider.connections}
                                    onChange={event => updateProvider(index, { connections: event.target.value })}
                                />
                            </Form.Group>

                            <div className={styles["justify-right"]}>
                                <Button
                                    className={styles["test-connection-button"]}
                                    variant={testButtonVariant}
                                    disabled={!isTestEnabled}
                                    onClick={() => onTestProvider(index)}
                                >
                                    {testButtonLabel}
                                </Button>
                            </div>
                        </div>
                    );
                })}
            </div>

            <Button
                variant="outline-primary"
                className={styles.addProviderButton}
                onClick={addProvider}
            >
                Add Provider
            </Button>

            <div className={styles.connectionsSummary}>
                Total Max Connections: {totalConnections || 0}
            </div>

            <Form.Group className={styles["form-group"]}>
                <Form.Label>Connections Per Stream</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="text"
                    value={connectionsPerStream}
                    isInvalid={Boolean(connectionsPerStreamError)}
                    onChange={event => setNewConfig(prev => ({
                        ...prev,
                        "usenet.connections-per-stream": event.target.value,
                    }))}
                />
                {connectionsPerStreamError && (
                    <Form.Control.Feedback type="invalid">
                        {connectionsPerStreamError}
                    </Form.Control.Feedback>
                )}
            </Form.Group>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
        || config["usenet.host"] !== newConfig["usenet.host"]
        || config["usenet.port"] !== newConfig["usenet.port"]
        || config["usenet.use-ssl"] !== newConfig["usenet.use-ssl"]
        || config["usenet.user"] !== newConfig["usenet.user"]
        || config["usenet.pass"] !== newConfig["usenet.pass"]
        || config["usenet.connections"] !== newConfig["usenet.connections"]
        || config["usenet.connections-per-stream"] !== newConfig["usenet.connections-per-stream"];
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}
