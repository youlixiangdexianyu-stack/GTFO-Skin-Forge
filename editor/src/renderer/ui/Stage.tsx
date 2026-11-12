import {
  ActionIcon,
  Button,
  ColorInput,
  Group,
  Kbd,
  Loader,
  Paper,
  Popover,
  SegmentedControl,
  Slider,
  Stack,
  Stepper,
  Switch,
  Text,
  Title,
  Tooltip,
} from '@mantine/core';
import { IconArrowsMove, IconFocusCentered, IconFolder, IconMagnet, IconRotate, IconRotate360, IconSun, IconZoomScan } from '@tabler/icons-react';
import { playSound } from '../audio';
import { triggersFor, type Trigger } from '../protocol';
import { useItem, useStore } from '../store';
import { simulate } from '../viewport/refs';
import { LiveryBar } from './LiveryPanel';
import { SkinStrip } from './SkinStrip';
import { WeaponTitle } from './WeaponGallery';
import { focusSelection, useModal, useViewStore, Viewport } from '../viewport/Viewport';
import { t } from '../i18n';

function FirstRun() {
  const game = useStore((s) => s.game);
  const ready = useStore((s) => s.ready);
  const weaponId = useStore((s) => s.weaponId);
  const choose = useStore((s) => s.chooseGameDir);
  const found = !!game?.found;
  if (ready && found && weaponId) return null;
  return (
    <div className="stage-empty">
      <Paper className="card" bg="transparent">
        {!ready ? (
          <Stack align="center">
            <Title order={2}>{t('stage.k1')}</Title>
            <Text c="dimmed">{t('stage.k2')}</Text>
            <Loader type="bars" size="sm" />
          </Stack>
        ) : (
          <Stack>
            <Title order={2}>{found ? t('stage.k3') : t('stage.k4')}</Title>
            <Text c="dimmed">{found ? t('stage.k5') : t('stage.k6')}</Text>
            <Stepper active={found ? 1 : 0} orientation="vertical" size="sm" color="flare">
              <Stepper.Step label={t('stage.k7')} />
              <Stepper.Step label={t('stage.k8')} />
              <Stepper.Step label={t('stage.k9')} />
              <Stepper.Step label={t('stage.k10')} />
            </Stepper>
            {!found && (
              <Button size="sm" leftSection={<IconFolder size={16} />} onClick={() => void choose()}>
                {t('stage.k11')}
              </Button>
            )}
          </Stack>
        )}
      </Paper>
    </div>
  );
}

function ViewBar() {
  const view = useViewStore();
  return (
    <Group className="overlay views" gap={4}>
      <Tooltip label={t('stage.k12')}>
        <SegmentedControl
          size="xs"
          value={view.ortho ? 'o' : 'p'}
          onChange={(v) => view.set({ ortho: v === 'o' })}
          data={[
            {
              value: 'p',
              get label() {
                return t('stage.k13');
              },
            },
            {
              value: 'o',
              get label() {
                return t('stage.k14');
              },
            },
          ]}
        />
      </Tooltip>
      <Tooltip label={t('stage.k15')}>
        <ActionIcon variant="subtle" color="gray" onClick={focusSelection} aria-label={t('stage.k16')}>
          <IconFocusCentered size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

function LightBar() {
  const prefs = useStore((s) => s.prefs);
  const setPrefs = useStore((s) => s.setPrefs);
  return (
    <Group className="overlay light" gap={4}>
      <Popover position="top-start" width={260} withArrow>
        <Popover.Target>
          <Tooltip label={t('stage.k17')}>
            <ActionIcon variant="subtle" color="gray" aria-label={t('stage.k17')}>
              <IconSun size={16} />
            </ActionIcon>
          </Tooltip>
        </Popover.Target>
        <Popover.Dropdown>
          <Stack gap="sm">
            <div>
              <Text size="xs" c="dimmed" mb={4}>
                {t('stage.k18')} {prefs.ambient.toFixed(2)}
                {t('stage.k19')}
              </Text>
              <Slider min={0} max={3} step={0.05} value={prefs.ambient} onChange={(v) => setPrefs({ ambient: v })} />
            </div>
            {/* Nested dropdown stays inside the popover; in a portal, clicks in it count as outside and close the popover. */}
            <ColorInput
              label={t('stage.k20')}
              value={prefs.ambientColor}
              onChange={(v) => setPrefs({ ambientColor: v })}
              onChangeEnd={(v) => setPrefs({ ambientColor: v })} // the picker's onChange lags one move (see fields Color)
              popoverProps={{ withinPortal: false }}
            />
            <Switch label={t('stage.k21')} checked={prefs.bloom} onChange={(e) => setPrefs({ bloom: e.currentTarget.checked })} />
          </Stack>
        </Popover.Dropdown>
      </Popover>
      <Tooltip label={t('stage.k22')}>
        <ActionIcon
          variant={prefs.autoRotate ? 'light' : 'subtle'}
          color={prefs.autoRotate ? 'flare' : 'gray'}
          onClick={() => setPrefs({ autoRotate: !prefs.autoRotate })}
          aria-label={t('stage.k22')}
        >
          <IconRotate360 size={16} />
        </ActionIcon>
      </Tooltip>
    </Group>
  );
}

function AttachTools() {
  const view = useViewStore();
  const snap = useStore((s) => s.snapToSurface);
  const selection = useStore((s) => s.selection);
  return (
    <Stack className="overlay tools" gap={4}>
      <SegmentedControl
        size="xs"
        orientation="vertical"
        value={view.gizmo}
        onChange={(v) => view.set({ gizmo: v as 'translate' })}
        data={[
          {
            value: 'translate',
            label: (
              <Group gap={4} wrap="nowrap">
                <IconArrowsMove size={14} />
                {t('stage.k23')} <Kbd size="xs">G</Kbd>
              </Group>
            ),
          },
          {
            value: 'rotate',
            label: (
              <Group gap={4} wrap="nowrap">
                <IconRotate size={14} />
                {t('stage.k24')} <Kbd size="xs">R</Kbd>
              </Group>
            ),
          },
          {
            value: 'scale',
            label: (
              <Group gap={4} wrap="nowrap">
                <IconZoomScan size={14} />
                {t('stage.k25')} <Kbd size="xs">S</Kbd>
              </Group>
            ),
          },
        ]}
      />
      {selection?.kind === 'attachment' && (
        <Tooltip label={t('stage.k26')}>
          <Button
            size="xs"
            variant={snap ? 'light' : 'subtle'}
            color={snap ? 'flare' : 'gray'}
            leftSection={<IconMagnet size={14} />}
            onClick={() => useStore.setState({ snapToSurface: !snap })}
          >
            {t('stage.k27')}
          </Button>
        </Tooltip>
      )}
    </Stack>
  );
}

function ModalHint() {
  const modal = useModal();
  const [tool, axis] = modal.split('|');
  if (tool === 'null') return null;
  const name = {
    get move() {
      return t('stage.k23');
    },
    get rotate() {
      return t('stage.k24');
    },
    get scale() {
      return t('stage.k25');
    },
  }[tool as 'move'];
  return (
    <div className="modal-hint">
      <b style={{ color: 'var(--flare)' }}>{name}</b>
      <span>
        {t('stage.k28')}
        {axis === 'null' ? (
          t('stage.k29')
        ) : (
          <b className={`axis-${axis}`}>
            {axis.toUpperCase()}
            {t('stage.k30')}
          </b>
        )}
      </span>
      <Text span size="xs" c="dimmed">
        <Kbd>X</Kbd> <Kbd>Y</Kbd> <Kbd>Z</Kbd> {t('stage.k31')}
        <Kbd>Enter</Kbd> {t('stage.k32')}
        <Kbd>Esc</Kbd> {t('stage.k33')}
      </Text>
    </div>
  );
}

/** Plays game events in the 3D view, exactly as the game triggers layers, attachment effects and sounds. */
function EventBar() {
  const item = useItem();
  const play = (x: Trigger) => {
    simulate(x);
    for (const s of useStore.getState().doc.sounds ?? []) if (s?.trigger === x && s.file) playSound(s);
  };
  return (
    <Group className="overlay events" gap={2}>
      {triggersFor(item)
        .filter((x) => x.simulate)
        .map((x) => (
          <Button key={x.value} size="compact-xs" variant="subtle" color="flare" onClick={() => play(x.value)}>
            {t('stage.simulate', { v0: x.label })}
          </Button>
        ))}
    </Group>
  );
}

export function Stage() {
  const preview = useStore((s) => s.preview);
  const loading = useStore((s) => s.previewLoading);
  return (
    <main className="stage">
      <Viewport />
      <div className="stage-head">
        <WeaponTitle />
        <SkinStrip />
      </div>
      {loading && (
        <div className="stage-empty">
          <Stack align="center" gap="xs">
            <Loader type="bars" size="sm" />
            <Text c="dimmed" ta="center">
              {t('stage.k34')}
              <br />
              {t('stage.k35')}
            </Text>
          </Stack>
        </div>
      )}
      <FirstRun />
      {preview && (
        <>
          <ViewBar />
          <LightBar />
          <AttachTools />
          <EventBar />
          <LiveryBar />
          <ModalHint />
        </>
      )}
    </main>
  );
}
