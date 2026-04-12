package encoding

// Option configures deserialization behavior for StatementReader and Unmarshal.
type Option func(*options)

type options struct {
	unknownFields FieldPolicy
	missingFields MissingPolicy
	duplicates    DuplicatePolicy
	converters    *converterRegistry
}

func defaultOptions() *options {
	return &options{
		unknownFields: SkipUnknown,
		missingFields: ZeroMissing,
		duplicates:    LastWins,
	}
}

func buildOptions(opts []Option) *options {
	o := defaultOptions()
	for _, fn := range opts {
		fn(o)
	}
	return o
}

// FieldPolicy controls behavior when PAKT data contains fields not present
// in the target struct.
type FieldPolicy int

const (
	// SkipUnknown silently skips unknown fields (default).
	SkipUnknown FieldPolicy = iota
	// ErrorUnknown returns an error on unknown fields.
	ErrorUnknown
)

// MissingPolicy controls behavior when the target struct has fields not
// present in the PAKT data.
type MissingPolicy int

const (
	// ZeroMissing leaves missing fields at their zero value (default).
	ZeroMissing MissingPolicy = iota
	// ErrorMissing returns an error for missing fields.
	ErrorMissing
)

// DuplicatePolicy controls behavior when PAKT data contains duplicate
// statement names or map keys.
type DuplicatePolicy int

const (
	// LastWins overwrites with the last value encountered (default).
	LastWins DuplicatePolicy = iota
	// FirstWins keeps the first value and ignores subsequent duplicates.
	FirstWins
	// ErrorDupes returns an error on duplicate names or keys.
	ErrorDupes
	// Accumulate appends duplicate values to a collection (target must be a slice).
	Accumulate
)

// UnknownFields sets the policy for unknown fields in PAKT data.
func UnknownFields(policy FieldPolicy) Option {
	return func(o *options) { o.unknownFields = policy }
}

// MissingFields sets the policy for target fields missing from PAKT data.
func MissingFields(policy MissingPolicy) Option {
	return func(o *options) { o.missingFields = policy }
}

// Duplicates sets the policy for duplicate statement names or map keys.
func Duplicates(policy DuplicatePolicy) Option {
	return func(o *options) { o.duplicates = policy }
}

// converterRegistry holds registered ValueConverters keyed by target type
// and named converters for field-level overrides.
type converterRegistry struct {
	byType map[any]any    // reflect.Type → ValueConverter (type-erased)
	byName map[string]any // converter name → ValueConverter (type-erased)
}

func (o *options) ensureConverters() *converterRegistry {
	if o.converters == nil {
		o.converters = &converterRegistry{
			byType: make(map[any]any),
			byName: make(map[string]any),
		}
	}
	return o.converters
}
