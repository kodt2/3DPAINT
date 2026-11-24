/**
 * Small presentational formatters shared by the SPA screens (step 3c.3
 * extracted them from LibraryPage for the viewer's info panel).
 */

export function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} Б`;
  }
  const units = ['КБ', 'МБ', 'ГБ'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toLocaleString('ru-RU', { maximumFractionDigits: 1 })} ${units[unit]}`;
}

export function formatDate(iso: string): string {
  if (!iso) {
    return '';
  }
  return new Date(iso).toLocaleDateString('ru-RU', {
    day: 'numeric',
    month: 'long',
    year: 'numeric',
  });
}

/** ru-RU number with a limited fraction part (info panel, dimensions). */
export function formatNumber(value: number, maximumFractionDigits = 3): string {
  return value.toLocaleString('ru-RU', { maximumFractionDigits });
}
