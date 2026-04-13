package encoding

import (
	"fmt"
	"strings"
	"testing"
)

// --- test converter types ---

type Celsius float64

type celsiusConverter struct{}

func (c celsiusConverter) FromPakt(vr *ValueReader, pt Type) (Celsius, error) {
	f, err := vr.FloatValue()
	if err != nil {
		return 0, err
	}
	return Celsius(f), nil
}

func (c celsiusConverter) ToPakt(enc *Encoder, v Celsius) error {
	return fmt.Errorf("ToPakt not implemented")
}

// --- tests ---

func TestRegisterConverterAndReadValue(t *testing.T) {
	input := "temp:float = 3.65e1\n"
	sr := NewUnitReader(strings.NewReader(input),
		RegisterConverter[Celsius](celsiusConverter{}))
	defer sr.Close()

	for stmt := range sr.Properties() {
		if stmt.Name != "temp" {
			t.Fatalf("expected 'temp', got %q", stmt.Name)
		}
		val, err := ReadValue[Celsius](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != Celsius(36.5) {
			t.Errorf("expected 36.5, got %v", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

func TestRegisterNamedConverter(t *testing.T) {
	// Verify RegisterNamedConverter stores the converter without panic.
	opt := RegisterNamedConverter("celsius", celsiusConverter{})
	o := defaultOptions()
	opt(o)
	if o.converters == nil {
		t.Fatal("expected converters to be initialized")
	}
	if _, ok := o.converters.byName["celsius"]; !ok {
		t.Error("expected 'celsius' converter to be registered")
	}
}

func TestValueReaderStringValue(t *testing.T) {
	tests := []struct {
		name    string
		input   string
		want    string
		wantErr bool
	}{
		{"valid", "msg:str = 'hello'\n", "hello", false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			sr := NewUnitReader(strings.NewReader(tt.input),
				RegisterConverter[string](stringViaVR{}))
			defer sr.Close()

			for range sr.Properties() {
				val, err := ReadValue[string](sr)
				if (err != nil) != tt.wantErr {
					t.Fatalf("err=%v, wantErr=%v", err, tt.wantErr)
				}
				if val != tt.want {
					t.Errorf("got %q, want %q", val, tt.want)
				}
			}
			if err := sr.Err(); err != nil {
				t.Fatal(err)
			}
		})
	}
}

type stringViaVR struct{}

func (s stringViaVR) FromPakt(vr *ValueReader, pt Type) (string, error) {
	return vr.StringValue()
}
func (s stringViaVR) ToPakt(enc *Encoder, v string) error { return nil }

func TestValueReaderIntValue(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("n:int = 42\n"),
		RegisterConverter[int64](intViaVR{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[int64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != 42 {
			t.Errorf("got %d, want 42", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type intViaVR struct{}

func (iv intViaVR) FromPakt(vr *ValueReader, pt Type) (int64, error) {
	return vr.IntValue()
}
func (iv intViaVR) ToPakt(enc *Encoder, v int64) error { return nil }

func TestValueReaderFloatValue(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("rate:float = 2.5e0\n"),
		RegisterConverter[float64](floatViaVR{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[float64](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != 2.5 {
			t.Errorf("got %f, want 2.5", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type floatViaVR struct{}

func (fv floatViaVR) FromPakt(vr *ValueReader, pt Type) (float64, error) {
	return vr.FloatValue()
}
func (fv floatViaVR) ToPakt(enc *Encoder, v float64) error { return nil }

func TestValueReaderBoolValue(t *testing.T) {
	tests := []struct {
		name    string
		input   string
		want    bool
		wantErr bool
	}{
		{"true", "flag:bool = true\n", true, false},
		{"false", "flag:bool = false\n", false, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			sr := NewUnitReader(strings.NewReader(tt.input),
				RegisterConverter[bool](boolViaVR{}))
			defer sr.Close()

			for range sr.Properties() {
				val, err := ReadValue[bool](sr)
				if (err != nil) != tt.wantErr {
					t.Fatalf("err=%v, wantErr=%v", err, tt.wantErr)
				}
				if val != tt.want {
					t.Errorf("got %v, want %v", val, tt.want)
				}
			}
			if err := sr.Err(); err != nil {
				t.Fatal(err)
			}
		})
	}
}

type boolViaVR struct{}

func (bv boolViaVR) FromPakt(vr *ValueReader, pt Type) (bool, error) {
	return vr.BoolValue()
}
func (bv boolViaVR) ToPakt(enc *Encoder, v bool) error { return nil }

func TestValueReaderDecValue(t *testing.T) {
	sr := NewUnitReader(strings.NewReader("price:dec = 19.99\n"),
		RegisterConverter[string](decViaVR{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != "19.99" {
			t.Errorf("got %q, want '19.99'", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type decViaVR struct{}

func (dv decViaVR) FromPakt(vr *ValueReader, pt Type) (string, error) {
	return vr.DecValue()
}
func (dv decViaVR) ToPakt(enc *Encoder, v string) error { return nil }

func TestValueReaderBytesValue(t *testing.T) {
	// Use hex-encoded binary
	sr := NewUnitReader(strings.NewReader("data:bin = x'48454c4c4f'\n"),
		RegisterConverter[[]byte](bytesViaVR{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[[]byte](sr)
		if err != nil {
			t.Fatal(err)
		}
		if string(val) != "HELLO" {
			t.Errorf("got %q, want 'HELLO'", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type bytesViaVR struct{}

func (bv bytesViaVR) FromPakt(vr *ValueReader, pt Type) ([]byte, error) {
	return vr.BytesValue()
}
func (bv bytesViaVR) ToPakt(enc *Encoder, v []byte) error { return nil }

func TestValueReaderIsNil(t *testing.T) {
	// Test IsNil returns false for non-nil values (nil values are intercepted before converter)
	sr := NewUnitReader(strings.NewReader("label:str = 'hello'\n"),
		RegisterConverter[string](nilAndErrCheckVR{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[string](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val != "hello" {
			t.Errorf("expected 'hello', got %q", val)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type nilAndErrCheckVR struct{}

func (n nilAndErrCheckVR) FromPakt(vr *ValueReader, pt Type) (string, error) {
	if vr.IsNil() {
		return "", nil
	}
	// Also exercise Err()
	if vr.Err() != nil {
		return "", vr.Err()
	}
	return vr.StringValue()
}
func (n nilAndErrCheckVR) ToPakt(enc *Encoder, v string) error { return nil }

func TestValueReaderBoolValueInvalidLiteral(t *testing.T) {
	// Force a converter that calls BoolValue on a non-boolean string
	sr := NewUnitReader(strings.NewReader("flag:str = 'notbool'\n"),
		RegisterConverter[bool](boolViaVR{}))
	defer sr.Close()

	for range sr.Properties() {
		_, err := ReadValue[bool](sr)
		if err == nil {
			t.Fatal("expected error for invalid bool literal")
		}
	}
}

func TestValueReaderStringValueOnNonScalar(t *testing.T) {
	// Converter receives a struct start event, StringValue should error
	sr := NewUnitReader(strings.NewReader("s:{x:int} = {1}\n"),
		RegisterConverter[dummy](structStringVR{}))
	defer sr.Close()

	for range sr.Properties() {
		_, err := ReadValue[dummy](sr)
		if err == nil {
			t.Fatal("expected error calling StringValue on non-scalar")
		}
	}
}

type structStringVR struct{}

func (sv structStringVR) FromPakt(vr *ValueReader, pt Type) (dummy, error) {
	_, err := vr.StringValue()
	return dummy{}, err
}
func (sv structStringVR) ToPakt(enc *Encoder, v dummy) error { return nil }

type dummy struct{}

// Test ReadAs — delegated deserialization from within a converter
type Wrapper struct {
	Inner string
}

func TestReadAsFromConverter(t *testing.T) {
	// The struct has 2 fields. The converter reads the struct start, then delegates each field.
	input := "data:{a:str, b:str} = {'hello', 'world'}\n"
	sr := NewUnitReader(strings.NewReader(input),
		RegisterConverter[Wrapper](structWrapperConverter{}))
	defer sr.Close()

	for range sr.Properties() {
		val, err := ReadValue[Wrapper](sr)
		if err != nil {
			t.Fatal(err)
		}
		if val.Inner != "hello+world" {
			t.Errorf("got %q, want 'hello+world'", val.Inner)
		}
	}
	if err := sr.Err(); err != nil {
		t.Fatal(err)
	}
}

type structWrapperConverter struct{}

func (sw structWrapperConverter) FromPakt(vr *ValueReader, pt Type) (Wrapper, error) {
	// We're positioned at StructStart. Read two string children via ReadAs.
	a, err := ReadAs[string](vr)
	if err != nil {
		return Wrapper{}, err
	}
	b, err := ReadAs[string](vr)
	if err != nil {
		return Wrapper{}, err
	}
	// Consume the struct end
	_ = vr.Skip()
	return Wrapper{Inner: a + "+" + b}, nil
}
func (sw structWrapperConverter) ToPakt(enc *Encoder, v Wrapper) error { return nil }
